"""server/reading.py: reading mode, segmenting, one voice per language, spelling out non-words.

The compatibility cases are the overlay's own C# tests (tests/EdgeTtsOverlay.Tests/Program.cs:
TestText, TestSegments, TestHardCap), because `prepare()`/`split()` must keep giving exactly what
the overlay gives. scripts/reading_parity/compare.py checks that on thousands of inputs.
"""
from __future__ import annotations

import unittest

from server import reading

TERMS = dict(reading.DEFAULT_PRONUNCIATIONS)
ZH, EN = "zh-TW-HsiaoChenNeural", "en-US-AvaNeural"


class CompatibilityTests(unittest.TestCase):
    """spell=False, no English voice: what the overlay does today."""

    def test_terms_are_spelled_out_but_not_inside_words(self):   # C# TestText
        value = reading.prepare("這是一段中文 mixed with English API、JSON、Python 3.12。organizerJSON 保持原樣。", True, TERMS)
        self.assertIn("A P I", value)
        self.assertIn("JSON", value)
        self.assertNotIn("J SON", value)
        self.assertIn("organizerJSON", value)

    def test_a_term_right_after_chinese_is_still_a_term(self):
        """The C# edges are an ASCII class; Python's \\b treats CJK as word characters."""
        self.assertEqual(reading.prepare("中文API與gpu", True, TERMS), "中文A P I與G P U")

    def test_markdown_links_code_and_urls_are_not_read_out(self):   # C# TestText
        value = reading.prepare("[文件](https://example.com) `term`\n```cs\nboom();\n```", True, TERMS)
        self.assertIn("文件", value)
        self.assertNotIn("https://", value)
        self.assertIn("程式碼區塊略過", value)

    def test_paths_keep_only_the_file_name(self):
        self.assertEqual(reading.prepare(r"改 C:\Users\x\main.py 與 /usr/bin/tool", True, {}), "改 main.py 與 tool")

    def test_the_path_boundary_follows_dotnet_not_python(self):
        self.assertEqual(reading.prepare(r"½C:\x\y.txt", True, {}), "½y.txt")
        self.assertEqual(reading.prepare("and/or", True, {}), "and/or")

    def test_a_backslash_in_a_pronunciation_is_taken_literally(self):
        self.assertEqual(reading.prepare("word", True, {"word": r"W\1"}), r"W\1")

    def test_paths_versions_and_abbreviations_do_not_end_a_sentence(self):   # C# TestSegments
        text = " ".join(["One sentence. Another sentence."] * 8) \
            + " 更新 src/app.py 後執行 npm run build。 Dr. Smith uses e.g. HTTP/2 and v1.2.3. Next sentence."
        parts = reading.split(text)
        self.assertGreater(len(parts), 2)
        self.assertTrue(any("src/app.py" in p for p in parts))
        self.assertTrue(any("Dr. Smith uses e.g. HTTP/2 and v1.2.3." in p for p in parts), parts)

    def test_text_without_punctuation_is_hard_cut(self):   # C# TestHardCap
        parts = reading.split("測" * 500)
        self.assertGreater(len(parts), 1)
        self.assertTrue(all(len(p) <= 120 for p in parts), [len(p) for p in parts])
        self.assertEqual("".join(parts), "測" * 500)

    def test_without_an_english_voice_segments_is_the_overlay_pipeline(self):
        text = "第一句 API。第二句。"
        self.assertEqual([s["text"] for s in reading.segments(text, voice=ZH, spell=False, pronunciations=TERMS)],
                         reading.split(reading.prepare(text, True, TERMS)))


class SpellOutTests(unittest.TestCase):
    CASES = {
        # acronyms and other non-words: letter by letter
        "GPU": "G P U", "VRAM": "V R A M", "SDXL": "S D X L", "GGUF": "G G U F", "UUID": "U U I D",
        "npm": "N P M", "ssh": "S S H", "llm": "L L M",
        # compounds: only the non-word part, letters kept apart from digits
        "ComfyUI": "Comfy U I", "vLLM": "V L L M", "OpenAI": "Open A I", "macOS": "mac O S",
        "k8s": "K 8 S", "gpt4o": "G P T 4 O", "RTX5060": "R T X 5060", "Qwen3": "Qwen 3", "H100": "H 100",
        # plurals of acronyms
        "GPUs": "G P U s", "LLMs": "L L M s",
        # words, including all-caps ones and names every voice says right: untouched
        "PyTorch": "PyTorch", "GitHub": "GitHub", "iPhone": "iPhone", "Qwen": "Qwen", "llama": "llama",
        "CUDA": "CUDA", "YAML": "YAML", "TODO": "TODO", "NOTE": "NOTE", "README": "README", "ASCII": "ASCII",
        "NVIDIA": "NVIDIA", "LoRA": "LoRA", "YAMLs": "YAMLs", "Mr": "Mr", "McDonald": "McDonald", "a": "a", "2024": "2024",
    }

    def test_each_case(self):
        for token, expected in self.CASES.items():
            with self.subTest(token=token):
                self.assertEqual(reading.spell_out(token), expected)

    def test_the_dictionary_wins_and_its_output_is_not_spelled_again(self):
        """JSON → JSON in the defaults means "say it as a word"; the rules alone would spell it."""
        self.assertEqual(reading.pronounce("JSON 和 GPU", TERMS, spell=True), "JSON 和 G P U")
        self.assertEqual(reading.pronounce("json", {}, spell=True), "json")

    def test_spelling_only_touches_latin_tokens(self):
        self.assertEqual(reading.pronounce("用 vLLM 跑 3 個模型", {}, spell=True), "用 V L L M 跑 3 個模型")


class LanguageTests(unittest.TestCase):
    def seg(self, text, **kw):
        return [(s["lang"], s["text"]) for s in reading.segments(text, voice=ZH, english_voice=EN,
                                                                 pronunciations=TERMS, **kw)]

    def test_an_english_sentence_inside_chinese_gets_the_english_voice(self):
        self.assertEqual(self.seg("這段的重點是：The quick brown fox jumps over the lazy dog. 然後再說明。"),
                         [("zh", "這段的重點是："), ("en", "The quick brown fox jumps over the lazy dog."),
                          ("zh", "然後再說明。")])

    def test_a_few_english_words_stay_with_the_chinese_voice(self):
        """Switching voices for one word leaves a gap on both sides of it."""
        self.assertEqual(self.seg("我們用的是 Stable Diffusion 跟 ComfyUI。"),
                         [("zh", "我們用的是 Stable Diffusion 跟 Comfy U I。")])

    def test_spelled_letters_do_not_count_as_english_words(self):
        self.assertEqual(self.seg("這張 GPU 的 VRAM 不夠。"), [("zh", "這張 G P U 的 V R A M 不夠。")])

    def test_a_text_with_no_chinese_is_all_english(self):
        self.assertEqual(self.seg("Hello. It is 2024."), [("en", "Hello. It is 2024.")])

    def test_every_segment_has_one_voice(self):
        segs = reading.segments("他說 I am fine today. 我們繼續。" * 20, voice=ZH, english_voice=EN, pronunciations=TERMS)
        self.assertTrue(all(s["voice"] == (EN if s["lang"] == "en" else ZH) for s in segs))
        self.assertTrue(any(s["lang"] == "en" for s in segs) and any(s["lang"] == "zh" for s in segs))

    def test_only_the_first_segment_is_short(self):
        segs = reading.segments("第一句話在這裡。" * 30, voice=ZH, english_voice=EN, pronunciations=TERMS)
        self.assertLessEqual(reading.weight(segs[0]["text"]), reading.FIRST_TARGET)
        self.assertGreater(reading.weight(segs[1]["text"]), reading.FIRST_TARGET)

    def test_the_threshold_is_adjustable(self):
        self.assertEqual(self.seg("我們用的是 Stable Diffusion。", min_english_words=2),
                         [("zh", "我們用的是"), ("en", "Stable Diffusion")])

    def test_a_run_of_punctuation_alone_is_not_a_segment(self):
        """Edge TTS returns no audio for 「。」 by itself, and the backend would answer 502."""
        segs = reading.segments("他說：I am fine today，。", voice=ZH, english_voice=EN, pronunciations=TERMS)
        self.assertTrue(all(any(c.isalnum() for c in s["text"]) for s in segs), segs)


if __name__ == "__main__":
    unittest.main()
