"""壓測 server/app.py：高併發請求＋大量 client disconnect。

跟 test_app.py 用一樣的手法：直接呼叫 `module._synthesize()` / `module.voices()`，用假的
Request（只有 `is_disconnected()`）取代真正的 ASGI 連線，全程 mock edge_tts，絕不連網。
不透過真的 uvicorn／HTTP —— 這台機器上（Windows + uvicorn 預設的 ProactorEventLoop）已經
證實 `_run_cancellable()`（server/app.py 的 `_wait_for_disconnect` 任務在工作完成後被取消時）
在「真的 TCP 連線＋有意義長度的合成延遲」下，即使完全沒有 client 中途斷線，也有相當高機率
（本機實測 15%～65%，視延遲長短而定）讓 client 永遠收不到回應（見這次壓測報告）。這支壓測腳本
的目的是量 `_gate`／`_voices_cache`／task 有沒有洩漏，跟那個「真連線會卡住」是兩個不同問題，
所以刻意繞過真的 HTTP，才不會被那個 bug 卡住而測不完。

不是 unittest：test.ps1 只跑 `python -m unittest server.test_app -v`，不會 discover 到這支，
所以加這支不影響既有測試清單。

用法：
    .venv\\Scripts\\python.exe -m server.stress_app <scenario> <n>

情境：
    concurrency   大量併發、全部正常完成的 /api/tts 請求，驗證同時活躍合成數 <= 2、slot 用完會釋放
    disconnect    大量請求在合成中途被（假造的）client 斷線打斷，驗證 slot 不洩漏、狀態不壞掉
    voices-flood  大量併發 /api/voices，驗證 _voices_cache 只打一次上游、不會重複累積
    mixed-fuzz    以上三種＋無效 payload 混著隨機打，驗證整體不洩漏、跑完仍可正常处理新請求

量測方式：暖機（n/20，捨棄）→ GC 後量一次基準（RSS 用 psutil、Python 配置用 tracemalloc）→
跑 n 次 → 再量一次，印出差值。
"""

from __future__ import annotations

import asyncio
import gc
import random
import sys
import time
import tracemalloc
from unittest.mock import patch

from server import app as module

try:
    import psutil

    _HAS_PSUTIL = True
except ImportError:  # pragma: no cover - psutil is optional
    _HAS_PSUTIL = False


class FakeCommunicate:
    """取代 edge_tts.Communicate：不連網，用短延遲模擬合成耗時，順便記錄併發峰值。"""

    active = 0
    max_active = 0
    calls = 0

    def __init__(self, text: str, voice: str, **kwargs) -> None:
        self.text = text
        self.voice = voice

    async def stream(self):
        cls = type(self)
        cls.calls += 1
        cls.active += 1
        cls.max_active = max(cls.max_active, cls.active)
        try:
            await asyncio.sleep(random.uniform(0.001, 0.01))
            yield {"type": "audio", "data": b"ID3-stress-audio"}
        finally:
            cls.active -= 1

    @classmethod
    def reset(cls) -> None:
        cls.active = 0
        cls.max_active = 0
        cls.calls = 0


class FakeRequest:
    """跟 test_app.py 的 Disconnected 同款：不連網，用時間旗標模擬 request.is_disconnected()。"""

    def __init__(self, disconnect_after: float | None = None) -> None:
        self._disconnect_after = disconnect_after
        self._start = time.monotonic()

    async def is_disconnected(self) -> bool:
        if self._disconnect_after is None:
            return False
        return (time.monotonic() - self._start) >= self._disconnect_after


def _measure_rss() -> int | None:
    if not _HAS_PSUTIL:
        return None
    gc.collect()
    return psutil.Process().memory_info().rss


def _make_payload(i: int) -> "module.TtsRequest":
    return module.TtsRequest(text=f"stress {i}", voice="zh-TW-Test")


CLIENT_CONCURRENCY = 20  # 同時在飛的請求數上限：模擬真實使用（多個分頁/快速連按），不是無節制打滿


async def scenario_concurrency(n: int) -> None:
    limiter = asyncio.Semaphore(CLIENT_CONCURRENCY)
    counts: dict[int, int] = {}

    async def one(i: int) -> None:
        async with limiter:
            try:
                r = await module._synthesize(_make_payload(i), FakeRequest())
                counts[r.status_code] = counts.get(r.status_code, 0) + 1
            except module.HTTPException as exc:
                # 429 是本地 slot 滿的正常容量行為（QUEUE_WAIT_SECONDS 逾時），不是洩漏；
                # 其他狀態碼才是意外。
                if exc.status_code != 429:
                    raise
                counts[429] = counts.get(429, 0) + 1

    await asyncio.gather(*(one(i) for i in range(n)))
    print(f"STRESS concurrency n={n} status_counts={counts} max_active_synth={FakeCommunicate.max_active}")
    if FakeCommunicate.max_active > 2:
        raise AssertionError(f"同時活躍合成超過上限：{FakeCommunicate.max_active}")


async def scenario_disconnect(n: int) -> None:
    completed = 0
    cancelled = 0
    busy_429 = 0

    async def one(i: int) -> None:
        nonlocal completed, cancelled, busy_429
        # 斷線時間點隨機落在「還在排隊」到「合成快做完」之間，盡量涵蓋各種競態時機。
        request = FakeRequest(disconnect_after=random.uniform(0.0, 0.015))
        try:
            r = await module._synthesize(_make_payload(i), request)
            if r.status_code == 200:
                completed += 1
        except module.ClientGone:
            cancelled += 1
        except module.HTTPException as exc:
            if exc.status_code == 429:
                busy_429 += 1
            else:
                raise

    await asyncio.gather(*(one(i) for i in range(n)))
    print(f"STRESS disconnect n={n} completed={completed} cancelled={cancelled} busy_429={busy_429}")


async def scenario_voices_flood(n: int) -> None:
    calls = {"n": 0}

    async def fake_list_voices():
        calls["n"] += 1
        await asyncio.sleep(0.001)
        return [{"ShortName": "zh-TW-Test", "Locale": "zh-TW", "Gender": "Female"}]

    with patch.object(module.edge_tts, "list_voices", fake_list_voices):
        # 先用一次「循序」呼叫把快取確實填好——voices() 在 cache 是 None 時完全沒有鎖，
        # 冷啟動當下同時打進來的併發請求會各自各打一次上游（thundering herd），這是量測到
        # 的既有行為、不是這次要驗的「累積」，所以先暖好快取，把兩件事分開量。
        cold_result = await module.voices()
        cold_calls = calls["n"]
        results = await asyncio.gather(*(module.voices() for _ in range(n)))
        warm_calls = calls["n"] - cold_calls

    if any(r != cold_result for r in results):
        raise AssertionError("併發下 /api/voices 回傳內容不一致")
    print(
        f"STRESS voices-flood n={n} cold_start_upstream_calls={cold_calls} "
        f"warm_cache_extra_upstream_calls={warm_calls}"
    )
    if warm_calls != 0:
        raise AssertionError(f"快取已熱仍重打上游：多打了 {warm_calls} 次（應該是 0）")


async def scenario_mixed_fuzz(n: int) -> None:
    rng = random.Random(7)
    counts: dict[str, int] = {}

    def bump(key: str) -> None:
        counts[key] = counts.get(key, 0) + 1

    async def one(i: int) -> None:
        choice = rng.random()
        try:
            if choice < 0.4:
                r = await module._synthesize(_make_payload(i), FakeRequest())
                bump(f"tts-{r.status_code}")
            elif choice < 0.65:
                r = await module._synthesize(_make_payload(i), FakeRequest(disconnect_after=rng.uniform(0.0, 0.01)))
                bump(f"tts-disconnect-completed-{r.status_code}")
            elif choice < 0.85:
                await module.voices()
                bump("voices-ok")
            else:
                try:
                    module.TtsRequest(text="", voice="zh-TW-Test")
                    bump("invalid-unexpectedly-accepted")
                except Exception:
                    bump("invalid-rejected")
        except module.ClientGone:
            bump("tts-cancelled")
        except module.HTTPException as exc:
            bump(f"tts-http-{exc.status_code}")

    with patch.object(module.edge_tts, "list_voices", AsyncListVoices()):
        await asyncio.gather(*(one(i) for i in range(n)))
    print(f"STRESS mixed-fuzz n={n} counts={counts}")


class AsyncListVoices:
    """給 mixed-fuzz 用的固定假清單（不用計次，voices-flood 才在乎呼叫次數）。"""

    async def __call__(self):
        await asyncio.sleep(0.001)
        return [{"ShortName": "zh-TW-Test", "Locale": "zh-TW", "Gender": "Female"}]


SCENARIOS = {
    "concurrency": scenario_concurrency,
    "disconnect": scenario_disconnect,
    "voices-flood": scenario_voices_flood,
    "mixed-fuzz": scenario_mixed_fuzz,
}


async def run(scenario: str, n: int) -> int:
    fn = SCENARIOS.get(scenario)
    if fn is None:
        print(f"未知情境：{scenario}（可用：{', '.join(SCENARIOS)}）", file=sys.stderr)
        return 2

    module._gate = asyncio.Semaphore(2)
    module._voices_cache = None
    module._voices_cached_at = 0.0
    FakeCommunicate.reset()

    tracemalloc.start()
    try:
        with patch.object(module.edge_tts, "Communicate", FakeCommunicate):
            warmup_n = max(2, n // 20)
            print(f"STRESS {scenario} warmup n={warmup_n}")
            await fn(warmup_n)

            module._gate = asyncio.Semaphore(2)
            module._voices_cache = None
            module._voices_cached_at = 0.0
            FakeCommunicate.reset()

            gc.collect()
            base_rss = _measure_rss()
            base_snapshot = tracemalloc.take_snapshot()

            start = time.monotonic()
            await fn(n)
            elapsed = time.monotonic() - start

            gc.collect()
            end_rss = _measure_rss()
            end_snapshot = tracemalloc.take_snapshot()

            # 壓測完，狀態還健康嗎：能不能再正常處理一次新請求。
            sanity = await module._synthesize(_make_payload(999999), FakeRequest())
            sanity_ok = sanity.status_code == 200
    finally:
        tracemalloc.stop()

    print(
        f"STRESS {scenario} iterations={n} elapsed_s={elapsed:.2f} gate_value={module._gate._value} "
        f"synth_calls={FakeCommunicate.calls} sanity_ok={sanity_ok}"
    )
    if base_rss is not None:
        print(f"STRESS {scenario} rss base={base_rss} end={end_rss} delta={end_rss - base_rss}")
    else:
        print(f"STRESS {scenario} rss unavailable (psutil not installed)")
    top = end_snapshot.compare_to(base_snapshot, "lineno")[:8]
    print(f"STRESS {scenario} tracemalloc top diffs (baseline -> after {n} iterations):")
    for stat in top:
        print(f"    {stat}")

    if module._gate._value != 2:
        raise AssertionError(f"semaphore slot 洩漏：value={module._gate._value}（預期 2）")
    if not sanity_ok:
        raise AssertionError("壓測後狀態壞掉：無法再正常處理新請求")

    print(f"STRESS {scenario} RESULT PASS")
    return 0


def main() -> int:
    if len(sys.argv) != 3:
        print("用法：python -m server.stress_app <scenario> <n>", file=sys.stderr)
        return 2
    scenario, n_str = sys.argv[1], sys.argv[2]
    try:
        n = int(n_str)
        if n <= 0:
            raise ValueError
    except ValueError:
        print("n 必須是正整數", file=sys.stderr)
        return 2
    try:
        return asyncio.run(run(scenario, n))
    except AssertionError as exc:
        print(f"STRESS {scenario} RESULT FAIL: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
