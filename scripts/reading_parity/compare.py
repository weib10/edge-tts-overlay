"""server/reading.py's compatibility path against the overlay's C#, sentence by sentence.

    .venv\\Scripts\\python.exe scripts\\reading_parity\\compare.py [--cases 5000] [--seed 20260920]

Run it after changing TextProcessor.cs, TextSegmenter.cs or the ported half of server/reading.py
(`prepare()` with spell=False, `split()`): until the overlay asks the backend how to read, both
must give the same output. The new rules (English voice, spelling out non-words) are off here.
Inputs are generated fake strings -- no clipboard, no network. Needs the .NET 8 SDK; the first
run builds into bin/ and obj/ (gitignored).

Known and accepted: emoji weigh 4 in C# (UTF-16 units) and 2 here (code points), which only moves
where a cut lands, so the random inputs carry no emoji. 2026-09-20, first run: 5,066 cases,
0 differences -- after fixing the one it found: the path pattern's (?<!\\w) means something
different in .NET and Python (½, ² and combining marks).
"""
from __future__ import annotations

import argparse
import json
import pathlib
import random
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent.parent))
from server import reading  # noqa: E402

FIXED = [
    "這是一段中文 mixed with English API、JSON、Python 3.12。organizerJSON 保持原樣。",
    "[文件](https://example.com) `term`\n```cs\nboom();\n```",
    " ".join(["One sentence. Another sentence."] * 8)
    + " 更新 src/app.py 後執行 npm run build。 Dr. Smith uses e.g. HTTP/2 and v1.2.3. Next sentence.",
    "測" * 500,
    "# 標題\n\n- 清單一\n- 清單二\n1. 第一\n2) 第二\n> 引言 **粗體** _斜體_ ~~刪除~~\n\n\n\n結尾",
    r"路徑 C:\Users\someone\demo\main.py 與 /usr/local/bin/tool 還有 ./rel/path.txt",
    "見 https://example.com/a?b=c 與 http://x.y/z）。版本 v2.0.1、3.14 與 U.S.A. 以及 e.g. 例子。",
    "Mr. Smith met Mrs. Jones at 3.30 p.m. They talked! Did they? Yes; really.",
    "中文，沒有句號但是有很多逗號，一直寫下去，" * 30,
    "word " * 400,
    "中文API與gpu還有Api和APIs以及_API_與API2",
]
ATOMS = ["中文", "長句子沒有標點", "API", "api", "json", "。", "！", "？", "；", "，", "、", "：", ":", ",", ".", "!", "?",
         " ", "  ", "\n", "\n\n\n", "\t", "\u3000", "\u00a0", "\x85", "\u2028", "Dr.", "e.g.", "ETC.", "v1.2.3", "3.14",
         "a.b.", "U.S.", "src/app.py", r"C:\x\y.txt", "a/b", "/x/y", "https://a.b/c", "www.example.com", "[t](https://u.v)",
         "```x```", "`c`", "# ", "##### ", "- ", "  - ", "1. ", "1) ", "> ", "**", "_", "~", "word", "Hello", "²", "½", "Ⅻ",
         "٣", "ｅ", "…", "\u0301", "e\u0301", "\u203f", "\uff3f", "測" * 60, "x" * 90, "字" * 45 + "，"]
TERM_SETS = [dict(reading.DEFAULT_PRONUNCIATIONS), {}, {"api": "a p i", "word": "W\\1", "C#": "C sharp", "": "x", "blank": " "}]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cases", type=int, default=5000)
    ap.add_argument("--seed", type=int, default=20260920)
    a = ap.parse_args()

    rng = random.Random(a.seed)
    cases = [{"text": t, "reading": r, "terms": terms} for t in FIXED for r in (True, False) for terms in TERM_SETS]
    for _ in range(a.cases):
        text = "".join(rng.choice(ATOMS) for _ in range(rng.randint(1, 100)))
        cases.append({"text": text, "reading": rng.random() < 0.7, "terms": rng.choice(TERM_SETS)})

    subprocess.run(["dotnet", "build", "-c", "Release", "-nologo", "-v", "q"], cwd=HERE, check=True,
                   stdout=subprocess.DEVNULL)
    with tempfile.TemporaryDirectory() as d:
        src, dst = pathlib.Path(d) / "in.json", pathlib.Path(d) / "out.json"
        src.write_text(json.dumps(cases, ensure_ascii=False), encoding="utf-8")
        subprocess.run(["dotnet", str(HERE / "bin/Release/net8.0/reading-parity.dll"), str(src), str(dst)], check=True)
        expected = json.loads(dst.read_text(encoding="utf-8"))

    bad = 0
    for case, want in zip(cases, expected):
        prepared = reading.prepare(case["text"], case["reading"], case["terms"])
        segments = reading.split(prepared)
        if prepared == want["prepared"] and segments == want["segments"]:
            continue
        bad += 1
        if bad <= 5:
            print("MISMATCH input:", json.dumps(case["text"][:100]))
            print("  python:", json.dumps(prepared[:160]), json.dumps(segments)[:240])
            print("  c#    :", json.dumps(want["prepared"][:160]), json.dumps(want["segments"])[:240])
    print(f"{len(cases)} cases, {bad} mismatches")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
