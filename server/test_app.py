import unittest
import asyncio
from unittest.mock import AsyncMock, patch

from server import app as module


class FakeCommunicate:
    calls = 0

    def __init__(self, *args, **kwargs):
        self.args = args
        self.kwargs = kwargs

    async def stream(self):
        FakeCommunicate.calls += 1
        yield {"type": "audio", "data": b"ID3-test-audio"}


class ApiTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        module._voices_cache = None
        module._voices_cached_at = 0.0
        module._gate = asyncio.Semaphore(2)

    async def test_health(self):
        self.assertEqual({"status": "ok"}, await module.health())

    async def test_tts_returns_mp3_and_formats_options(self):
        with patch.object(module.edge_tts, "Communicate", FakeCommunicate):
            response = await module._synthesize(
                module.TtsRequest(text="這個 API 使用 Python 3.12。", voice="zh-TW-HsiaoChenNeural", rate=5)
            )
        self.assertEqual("audio/mpeg", response.media_type)
        self.assertEqual(b"ID3-test-audio", response.body)

    async def test_voice_list_is_cached(self):
        mock = AsyncMock(return_value=[{"ShortName": "zh-TW-Test", "Locale": "zh-TW", "Gender": "Female"}])
        with patch.object(module.edge_tts, "list_voices", mock):
            first = await module.voices()
            second = await module.voices()
        self.assertEqual(first, second)
        self.assertEqual(1, mock.await_count)

    async def test_voice_list_retries_once_before_failing(self):
        mock = AsyncMock(side_effect=[ConnectionError("transient"), [{"ShortName": "zh-TW-Test", "Locale": "zh-TW"}]])
        with patch.object(module.edge_tts, "list_voices", mock):
            result = await module.voices()
        self.assertEqual(2, mock.await_count)
        self.assertEqual("zh-TW-Test", result[0]["id"])

    async def test_voice_list_502_only_after_both_attempts_fail(self):
        mock = AsyncMock(side_effect=ConnectionError("down"))
        with patch.object(module.edge_tts, "list_voices", mock):
            with self.assertRaises(module.HTTPException) as caught:
                await module.voices()
        self.assertEqual(502, caught.exception.status_code)
        self.assertEqual(2, mock.await_count)

    async def test_retry_once_after_temporary_failure(self):
        class Flaky(FakeCommunicate):
            calls = 0
            async def stream(self):
                Flaky.calls += 1
                if Flaky.calls == 1:
                    raise ConnectionError("temporary")
                yield {"type": "audio", "data": b"ID3-retried"}
        with patch.object(module.edge_tts, "Communicate", Flaky):
            response = await module._synthesize(module.TtsRequest(text="test", voice="test"))
        self.assertEqual(b"ID3-retried", response.body)
        self.assertEqual(2, Flaky.calls)

    async def test_validation_limits(self):
        with self.assertRaises(Exception):
            module.TtsRequest(text="x" * 5001, voice="test")

    async def test_capacity_returns_429(self):
        module._gate = asyncio.Semaphore(0)
        old_wait = module.QUEUE_WAIT_SECONDS
        module.QUEUE_WAIT_SECONDS = 0.01
        try:
            with self.assertRaises(module.HTTPException) as caught:
                await module._synthesize(module.TtsRequest(text="test", voice="test"))
            self.assertEqual(429, caught.exception.status_code)
            self.assertEqual("busy", caught.exception.headers["X-Edge-Tts-Error"])
        finally:
            module.QUEUE_WAIT_SECONDS = old_wait

    async def test_timeout_retries_then_504_and_releases_slot(self):
        async def slow(_request):
            await asyncio.sleep(1)
        old_timeout = module.SYNTHESIS_TIMEOUT_SECONDS
        module.SYNTHESIS_TIMEOUT_SECONDS = 0.01
        try:
            with patch.object(module, "_render", slow):
                with self.assertRaises(module.HTTPException) as caught:
                    await module._synthesize(module.TtsRequest(text="test", voice="test"))
            self.assertEqual(504, caught.exception.status_code)
            self.assertEqual(2, module._gate._value)
        finally:
            module.SYNTHESIS_TIMEOUT_SECONDS = old_timeout

    async def test_cancellation_stops_render_and_releases_slot(self):
        cancelled = asyncio.Event()
        entered = asyncio.Event()
        async def never(_request):
            entered.set()
            try:
                await asyncio.sleep(10)
            finally:
                cancelled.set()
        with patch.object(module, "_render", never):
            task = asyncio.create_task(module._synthesize(module.TtsRequest(text="test", voice="test")))
            await asyncio.wait_for(entered.wait(), timeout=1)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        self.assertTrue(cancelled.is_set())
        self.assertEqual(2, module._gate._value)

    async def test_queued_disconnect_does_not_leak_slot(self):
        class Disconnected:
            def __init__(self):
                self.gone = asyncio.Event()
            async def receive(self):
                await self.gone.wait()
                return {"type": "http.disconnect"}
        request = Disconnected()
        await module._gate.acquire()
        await module._gate.acquire()
        task = asyncio.create_task(module._synthesize(module.TtsRequest(text="test", voice="test"), request))
        await asyncio.sleep(0.02)
        request.gone.set()
        with self.assertRaises(module.ClientGone):
            await task
        self.assertEqual(0, module._gate._value)
        module._gate.release(); module._gate.release()

    async def test_acquire_boundary_releases_owned_slot(self):
        module._gate = asyncio.Semaphore(0)
        async def release_soon():
            await asyncio.sleep(0.02)
            module._gate.release()
        release = asyncio.create_task(release_soon())
        with patch.object(module.edge_tts, "Communicate", FakeCommunicate):
            response = await module._synthesize(module.TtsRequest(text="test", voice="test"))
        await release
        self.assertEqual(200, response.status_code)
        self.assertEqual(1, module._gate._value)

    async def test_cancel_between_acquire_and_wait_return_releases_slot(self):
        module._gate = asyncio.Semaphore(1)
        original_wait = asyncio.wait
        async def cancel_after_wait(*args, **kwargs):
            result = await original_wait(*args, **kwargs)
            asyncio.current_task().cancel()
            await asyncio.sleep(0)
            return result
        with patch.object(module.asyncio, "wait", cancel_after_wait):
            with self.assertRaises(asyncio.CancelledError):
                async with module._synthesis_slot(None):
                    pass
        self.assertEqual(1, module._gate._value)

    async def test_more_than_two_requests_queue_and_all_succeed(self):
        active = 0
        max_active = 0
        async def render(_request):
            nonlocal active, max_active
            active += 1; max_active = max(max_active, active)
            try:
                await asyncio.sleep(0.03)
                return b"ID3-audio"
            finally:
                active -= 1
        with patch.object(module, "_render", render):
            responses = await asyncio.gather(*(module._synthesize(module.TtsRequest(text=str(i), voice="test")) for i in range(5)))
        self.assertTrue(all(response.status_code == 200 for response in responses))
        self.assertEqual(2, max_active)


if __name__ == "__main__":
    unittest.main()
