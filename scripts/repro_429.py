"""Reproduce replace-style cancellation against a running local server."""
import asyncio
import collections

import httpx

URL = "http://127.0.0.1:8766/api/tts"
PAYLOAD = {
    "text": "這是非敏感的取消與取代測試，完成後播放下一句。",
    "voice": "zh-TW-HsiaoChenNeural",
    "rate": 0,
    "volume": 0,
    "pitch": 0,
}


async def one_round(client: httpx.AsyncClient) -> list[tuple[int, str]]:
    old = [asyncio.create_task(client.post(URL, json=PAYLOAD)) for _ in range(2)]
    await asyncio.sleep(0.02)
    for task in old:
        task.cancel()
    await asyncio.gather(*old, return_exceptions=True)
    responses = await asyncio.gather(*(client.post(URL, json=PAYLOAD) for _ in range(2)))
    return [(response.status_code, response.text[:100]) for response in responses]


async def main() -> None:
    counts: collections.Counter[int] = collections.Counter()
    details: collections.Counter[str] = collections.Counter()
    async with httpx.AsyncClient(timeout=15) as client:
        for round_number in range(5):
            round_results = await one_round(client)
            print(f"round={round_number + 1} statuses={[status for status, _ in round_results]}", flush=True)
            for status, detail in round_results:
                counts[status] += 1
                if status != 200:
                    details[detail] += 1
    print("statuses", dict(counts))
    print("errors", dict(details))
    if counts[429]:
        raise SystemExit(1)


if __name__ == "__main__":
    asyncio.run(main())
