"""Live-connection regression tests: the assertions a fake Request cannot make.

These start a real uvicorn against the real app with `edge_tts` mocked out, so nothing
reaches Microsoft. `test_app.py` drives `_synthesize()` with a stub whose
`receive()` is a plain coroutine, which is why 14 green tests still missed a bug where a
finished response never reached the client: it only shows up over a real ASGI channel.

The module doubles as the uvicorn target (`uvicorn server.test_live:app`).
"""
from __future__ import annotations

import asyncio
import json
import os
import socket
import subprocess
import sys
import time
import unittest
import urllib.error
import urllib.request
from pathlib import Path

import server.app as module

PORT = int(os.environ.get("EDGE_TTS_TEST_PORT", "8799"))
FAKE_DELAY = float(os.environ.get("EDGE_TTS_FAKE_DELAY", "0.05"))
PROJECT_ROOT = Path(__file__).resolve().parent.parent
PAYLOAD = {"text": "固定的測試例句。", "voice": "zh-TW-HsiaoChenNeural"}


class _FakeCommunicate:
    """Stands in for edge_tts.Communicate; the delay is what exposes the receive() race."""

    def __init__(self, *args, **kwargs):
        pass

    async def stream(self):
        await asyncio.sleep(FAKE_DELAY)
        yield {"type": "audio", "data": b"\0" * 8192}


module.edge_tts.Communicate = _FakeCommunicate
app = module.app


def _post(timeout: float = 10.0) -> int:
    request = urllib.request.Request(
        f"http://127.0.0.1:{PORT}/api/tts",
        data=json.dumps(PAYLOAD).encode(),
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            response.read()
            return response.status
    except urllib.error.HTTPError as exc:
        return exc.code


def _abandon_request() -> None:
    """Send a request and drop the socket mid-synthesis, the way skip/replace does."""
    body = json.dumps(PAYLOAD).encode()
    connection = socket.create_connection(("127.0.0.1", PORT), timeout=5)
    try:
        connection.sendall(
            b"POST /api/tts HTTP/1.1\r\nHost: 127.0.0.1\r\n"
            b"Content-Type: application/json\r\n"
            b"Content-Length: " + str(len(body)).encode() + b"\r\n\r\n" + body
        )
        time.sleep(FAKE_DELAY / 2)
    finally:
        connection.close()


class LiveConnectionTests(unittest.TestCase):
    server: subprocess.Popen

    @classmethod
    def setUpClass(cls):
        with socket.socket() as probe:
            if probe.connect_ex(("127.0.0.1", PORT)) == 0:
                raise unittest.SkipTest(f"port {PORT} is already in use")
        cls.server = subprocess.Popen(
            [sys.executable, "-m", "uvicorn", "server.test_live:app",
             # warning on purpose: a dropped connection must stay quiet. If ClientGone
             # ever regresses to an unhandled error, uvicorn's traceback shows up here.
             "--host", "127.0.0.1", "--port", str(PORT), "--log-level", "warning"],
            cwd=str(PROJECT_ROOT),
        )
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if cls.server.poll() is not None:
                raise RuntimeError(f"test server exited early: {cls.server.returncode}")
            try:
                with urllib.request.urlopen(f"http://127.0.0.1:{PORT}/health", timeout=1) as response:
                    if response.status == 200:
                        return
            except Exception:
                time.sleep(0.25)
        raise RuntimeError("test server did not become healthy")

    @classmethod
    def tearDownClass(cls):
        cls.server.terminate()
        try:
            cls.server.wait(timeout=10)
        except subprocess.TimeoutExpired:
            cls.server.kill()

    def test_plain_requests_never_hang(self):
        """Regression: polling request.is_disconnected() wedged ~17% of these."""
        statuses = [_post() for _ in range(25)]
        self.assertEqual([200] * 25, statuses)

    def test_abandoned_requests_do_not_leak_slots(self):
        for _ in range(4):
            _abandon_request()
        time.sleep(FAKE_DELAY * 4)
        # Only two synthesis slots exist; if the dropped requests kept theirs, this 429s.
        self.assertEqual(200, _post())
        self.assertEqual(200, _post())


if __name__ == "__main__":
    unittest.main()
