"""Prove the old 50 ms queue deadline rejects the same short replace workload."""
import asyncio
import collections
import pathlib
import sys
from contextlib import asynccontextmanager

import httpx
import uvicorn

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
from server import app as module

URL = "http://127.0.0.1:8767/api/tts"
PAYLOAD = {
    "text": "這是非敏感的取消與取代測試，完成後播放下一句。",
    "voice": "zh-TW-HsiaoChenNeural",
    "rate": 0,
    "volume": 0,
    "pitch": 0,
}


async def one_round(client: httpx.AsyncClient) -> list[int]:
    old = [asyncio.create_task(client.post(URL, json=PAYLOAD)) for _ in range(2)]
    await asyncio.sleep(0.02)
    for task in old:
        task.cancel()
    await asyncio.gather(*old, return_exceptions=True)
    return [response.status_code for response in await asyncio.gather(*(client.post(URL, json=PAYLOAD) for _ in range(2)))]


async def main() -> int:
    @asynccontextmanager
    async def legacy_slot(request):
        try:
            await asyncio.wait_for(module._run_cancellable(module._gate.acquire(), request), timeout=0.05)
        except TimeoutError as exc:
            raise module.HTTPException(status_code=429, detail="目前合成工作已滿，請稍後再試") from exc
        try:
            yield
        finally:
            module._gate.release()

    async def fixed_slow_render(_request):
        await asyncio.sleep(0.2)
        return b"ID3-fixed-test-audio"

    module._synthesis_slot = legacy_slot
    module._render = fixed_slow_render
    server = uvicorn.Server(uvicorn.Config(module.app, host="127.0.0.1", port=8767, log_level="critical"))
    server_task = asyncio.create_task(server.serve())
    while not server.started:
        await asyncio.sleep(0.01)
    counts: collections.Counter[int] = collections.Counter()
    try:
        async with httpx.AsyncClient(timeout=15) as client:
            for round_number in range(20):
                statuses = await one_round(client)
                counts.update(statuses)
                print(f"round={round_number + 1} statuses={statuses}", flush=True)
    finally:
        server.should_exit = True
        await server_task
    print("statuses", dict(counts))
    return 0 if counts[429] else 1


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
