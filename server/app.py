from __future__ import annotations

import asyncio
import random
import time
from contextlib import asynccontextmanager
from typing import Any

import edge_tts
from fastapi import FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import Response
from pydantic import BaseModel, Field

from server import reading

app = FastAPI(title="Edge TTS Local", version="1.0.0")
app.add_middleware(
    CORSMiddleware,
    allow_origins=[],
    allow_credentials=False,
    allow_methods=[],
    allow_headers=[],
)

_gate = asyncio.Semaphore(2)
_voices_cache: list[dict[str, Any]] | None = None
_voices_cached_at = 0.0
VOICE_CACHE_SECONDS = 24 * 60 * 60
SYNTHESIS_TIMEOUT_SECONDS = 30
VOICE_LIST_TIMEOUT_SECONDS = 15
QUEUE_WAIT_SECONDS = 2.0


class ClientGone(Exception):
    """The client dropped the connection; there is nobody left to answer."""


class TtsRequest(BaseModel):
    text: str = Field(min_length=1, max_length=5000)
    voice: str = Field(min_length=1, max_length=128)
    rate: int = Field(default=0, ge=-50, le=100)
    volume: int = Field(default=0, ge=-50, le=50)
    pitch: int = Field(default=0, ge=-50, le=50)


class PrepareRequest(BaseModel):
    text: str = Field(min_length=1, max_length=20_000)
    voice: str = Field(min_length=1, max_length=128)
    english_voice: str | None = Field(default=None, max_length=128)
    reading_mode: bool = True
    pronunciations: dict[str, str] | None = None   # None = the overlay's defaults
    spell: bool = True
    min_english_words: int = Field(default=3, ge=1, le=50)


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/api/prepare")
async def prepare_text(payload: PrepareRequest) -> dict[str, Any]:
    """Text → the segments to synthesize, each with the voice that should read it (server/reading.py).

    Nothing leaves the machine here: this only decides how the text will be read.
    """
    if not payload.text.strip():
        raise HTTPException(status_code=422, detail="文字不能是空白")
    return {"segments": reading.segments(
        payload.text, voice=payload.voice, english_voice=payload.english_voice or None,
        reading_mode=payload.reading_mode, pronunciations=payload.pronunciations, spell=payload.spell,
        min_english_words=payload.min_english_words)}


async def _list_voices() -> list[dict[str, Any]]:
    """One bounded retry so a single transient failure never becomes a 502."""
    last_error: Exception = RuntimeError("語音清單沒有嘗試過")
    for attempt in range(2):
        try:
            return await asyncio.wait_for(edge_tts.list_voices(), timeout=VOICE_LIST_TIMEOUT_SECONDS)
        except Exception as exc:
            last_error = exc
            if attempt == 0:
                await asyncio.sleep(0.2 + random.random() * 0.1)
    raise last_error


@app.get("/api/voices")
async def voices() -> list[dict[str, Any]]:
    global _voices_cache, _voices_cached_at
    if _voices_cache is not None and time.monotonic() - _voices_cached_at < VOICE_CACHE_SECONDS:
        return _voices_cache

    try:
        remote = await _list_voices()
    except Exception as exc:
        if _voices_cache is not None:
            return _voices_cache
        raise HTTPException(status_code=502, detail="無法取得 Microsoft 語音清單") from exc

    _voices_cache = [
        {
            "id": voice.get("ShortName", ""),
            "locale": voice.get("Locale", ""),
            "name": voice.get("FriendlyName", voice.get("ShortName", "")),
            "gender": voice.get("Gender", ""),
        }
        for voice in remote
    ]
    _voices_cached_at = time.monotonic()
    return _voices_cache


async def _render(request: TtsRequest) -> bytes:
    communicate = edge_tts.Communicate(
        request.text.strip(),
        request.voice,
        rate=f"{request.rate:+d}%",
        volume=f"{request.volume:+d}%",
        pitch=f"{request.pitch:+d}Hz",
    )
    audio = bytearray()
    async for chunk in communicate.stream():
        if chunk.get("type") == "audio":
            audio.extend(chunk["data"])
    if not audio:
        raise RuntimeError("Microsoft 沒有回傳音訊")
    return bytes(audio)


async def _wait_for_disconnect(request: Request) -> None:
    """Wait on the ASGI receive channel rather than polling.

    `request.is_disconnected()` wraps every probe in a microsecond `wait_for`, so polling
    it cancels an in-flight uvicorn `receive()` twenty times a second. That race wedges
    the connection: synthesis finishes but the response never reaches the client (locally
    reproduced at 7/40 plain sequential requests). Awaiting the channel needs no timeout.
    """
    while True:
        message = await request.receive()
        if message.get("type") == "http.disconnect":
            return


async def _run_cancellable(coro: Any, request: Request | None) -> Any:
    work = asyncio.create_task(coro)
    disconnected = asyncio.create_task(_wait_for_disconnect(request)) if request else None
    try:
        waiters = {work} | ({disconnected} if disconnected else set())
        done, _ = await asyncio.wait(waiters, return_when=asyncio.FIRST_COMPLETED)
        if work in done:
            return await work
        if disconnected and disconnected in done:
            work.cancel()
            await asyncio.gather(work, return_exceptions=True)
            raise ClientGone
    finally:
        if not work.done():
            work.cancel()
            await asyncio.gather(work, return_exceptions=True)
        if disconnected:
            disconnected.cancel()
            await asyncio.gather(disconnected, return_exceptions=True)


@asynccontextmanager
async def _synthesis_slot(request: Request | None):
    """Acquire exactly one active slot; queued callers remain cancellable."""
    acquire = asyncio.create_task(_gate.acquire())
    disconnected = asyncio.create_task(_wait_for_disconnect(request)) if request else None
    acquired = False
    try:
        waiters = {acquire} | ({disconnected} if disconnected else set())
        done, _ = await asyncio.wait(waiters, timeout=QUEUE_WAIT_SECONDS, return_when=asyncio.FIRST_COMPLETED)
        # Prefer a completed acquire when disconnect and acquire race each other.
        if acquire in done:
            await acquire
            acquired = True
        elif disconnected and disconnected in done:
            raise ClientGone
        else:
            raise HTTPException(status_code=429, detail="目前合成工作已滿，請稍後再試", headers={"X-Edge-Tts-Error": "busy"})
        yield
    finally:
        if not acquire.done():
            acquire.cancel()
        await asyncio.gather(acquire, return_exceptions=True)
        if disconnected:
            disconnected.cancel()
            await asyncio.gather(disconnected, return_exceptions=True)
        owns_slot = acquired or (
            acquire.done() and not acquire.cancelled()
            and acquire.exception() is None and acquire.result() is True
        )
        if owns_slot:
            _gate.release()


async def _synthesize(payload: TtsRequest, request: Request | None = None) -> Response:
    if not payload.text.strip():
        raise HTTPException(status_code=422, detail="文字不能是空白")

    async with _synthesis_slot(request):
        last_error: Exception | None = None
        for attempt in range(2):
            try:
                audio = await _run_cancellable(
                    asyncio.wait_for(_render(payload), timeout=SYNTHESIS_TIMEOUT_SECONDS), request)
                return Response(content=audio, media_type="audio/mpeg")
            except ClientGone:
                raise
            except TimeoutError as exc:
                last_error = exc
                if attempt == 1:
                    raise HTTPException(status_code=504, detail="語音合成逾時") from exc
                await asyncio.sleep(0.2 + random.random() * 0.1)
            except Exception as exc:
                last_error = exc
                if attempt == 1:
                    raise HTTPException(status_code=502, detail="Microsoft 語音服務暫時失敗，請稍後重試") from exc
                await asyncio.sleep(0.2 + random.random() * 0.1)
        raise HTTPException(status_code=502, detail="Microsoft 語音服務暫時失敗，請稍後重試") from last_error


@app.post("/api/tts")
async def synthesize(payload: TtsRequest, request: Request) -> Response:
    try:
        return await _synthesize(payload, request)
    except ClientGone:
        # Skip and replace disconnect mid-synthesis all the time. Answering the closed
        # socket with 499 keeps uvicorn from logging a full ASGI traceback for each one,
        # which would otherwise bury PythonServer.LastError's startup diagnostics.
        return Response(status_code=499)
