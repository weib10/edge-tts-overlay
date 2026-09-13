import asyncio
import pathlib
import sys
import time

import edge_tts


async def main() -> int:
    started = time.perf_counter()
    voices = await edge_tts.list_voices()
    voice = "zh-TW-HsiaoChenNeural"
    if voice not in {item["ShortName"] for item in voices}:
        print(f"missing voice: {voice}", file=sys.stderr)
        return 2
    audio = bytearray()
    communicate = edge_tts.Communicate(
        "這是一段非敏感的語音合成測試。The build completed successfully.", voice
    )
    async for chunk in communicate.stream():
        if chunk["type"] == "audio":
            audio.extend(chunk["data"])
    if len(audio) < 1000:
        print("audio response too small", file=sys.stderr)
        return 3
    output = pathlib.Path(__file__).resolve().parent.parent / "smoke.mp3"
    output.write_bytes(audio)
    print(f"voice=yes bytes={len(audio)} seconds={time.perf_counter() - started:.2f} path={output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
