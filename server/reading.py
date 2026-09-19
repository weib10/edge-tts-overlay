"""How text becomes speech: reading-mode cleanup, one voice per language, spelling out non-words.

The overlay still does the first part itself in C# (Services/TextProcessor.cs and
Services/TextSegmenter.cs). `prepare()` and `split()` here are exact ports of those two files --
`scripts/reading_parity/compare.py` runs both on thousands of generated inputs and expects no
difference. Two things are layered on top, and both are off in that compatibility path:

* **English gets an English voice.** A Chinese voice reading a whole English sentence is hard to
  follow. A run of `min_english_words` or more Latin words is read by `english_voice`, and so is
  every sentence of a text with no CJK in it at all. Shorter runs -- a product name inside a
  Chinese sentence -- stay with the Chinese voice: each switch is a separate clip, and switching
  for a single word leaves an audible gap on both sides of it.
* **Non-words are spelled out** (`spell=True`): `GPU`, `vLLM`, `k8s`, `npm` are read letter by
  letter instead of being guessed at. The pronunciation dictionary still wins, and an entry that
  maps a term to itself (the default `"JSON": "JSON"`) keeps that term from being spelled.
"""
from __future__ import annotations

import re
import unicodedata

# Same as AppSettings.DefaultPronunciations() in the overlay.
DEFAULT_PRONUNCIATIONS = {
    "API": "A P I", "GPU": "G P U", "CPU": "C P U", "JSON": "JSON",
    "CLI": "C L I", "SDK": "S D K", "IDE": "I D E", "UI": "U I",
    "UX": "U X", "HTTP": "H T T P", "HTTPS": "H T T P S", "URL": "U R L",
    "HTML": "H T M L", "CSS": "C S S", "SQL": "S Q L",
}


# ── Reading mode: TextProcessor.Prepare ─────────────────────────────────────

_CODE_BLOCK = re.compile(r"```[\s\S]*?```")
_MD_LINK = re.compile(r"\[([^\]]+)\]\(https?://[^\s)]+\)")
_URL = re.compile(r"https?://\S+")
# The C# pattern starts with (?<!\w), but .NET's \w is [\p{L}\p{Mn}\p{Nd}\p{Pc}] and Python's is
# not (Python counts ½ and ² as word characters, .NET does not; combining marks the other way
# round). So the lookbehind is dropped here and checked character by character in _strip_paths.
_PATH = re.compile(r'(?:[A-Za-z]:\\|/)(?:[^\s<>:"|?*]+[/\\])*([^\s/\\<>:"|?*]+)')
_PATH_START = re.compile(r"[A-Za-z]:\\|/")
_DOTNET_WORD = {"Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Nd", "Pc"}
_MD_PREFIX = re.compile(r"^\s{0,3}(?:#{1,6}\s*|[-*+]\s+|\d+[.)]\s+|>\s*)", re.M)
_MD_DECORATION = re.compile(r"[*_~]{1,3}")
_SPACES = re.compile(r"[ \t]{2,}")
_NEWLINES = re.compile(r"\n{3,}")


def _strip_paths(text: str) -> str:
    """`PathRegex().Replace(text, "$1")`: a path is read as its file name.

    Tries each start position left to right like the regex engine, skipping a whole match once
    it is replaced, with the lookbehind decided by .NET's definition of a word character.
    """
    out, pos, i = [], 0, 0
    while (start := _PATH_START.search(text, i)) is not None:
        i = start.start()
        before = text[i - 1] if i else ""
        m = None if before and unicodedata.category(before) in _DOTNET_WORD else _PATH.match(text, i)
        if m is None:
            i += 1
            continue
        out += [text[pos:i], m.group(1)]
        pos = i = m.end()
    out.append(text[pos:])
    return "".join(out)


def _reading_mode(text: str) -> str:
    text = _CODE_BLOCK.sub("\n程式碼區塊略過。\n", text)
    text = _MD_LINK.sub(r"\1", text)
    text = _URL.sub("網址略過", text)
    text = _strip_paths(text)
    text = _MD_PREFIX.sub("", text)
    text = _MD_DECORATION.sub("", text)
    return text.replace("`", "")


def _normalize(text: str) -> str:
    return _NEWLINES.sub("\n\n", _SPACES.sub(" ", text)).strip()


def clean(text: str, reading_mode: bool) -> str:
    """Everything `Prepare` does except the dictionary: line endings, reading mode, whitespace."""
    text = text.replace("\r\n", "\n").strip()
    return _normalize(_reading_mode(text) if reading_mode else text)


def prepare(text: str, reading_mode: bool, pronunciations: dict[str, str], spell: bool = False) -> str:
    """`TextProcessor.Prepare`. With `spell=False` the output is exactly the overlay's."""
    text = text.replace("\r\n", "\n").strip()
    if reading_mode:
        text = _reading_mode(text)
    return _normalize(pronounce(text, pronunciations, spell))


# ── Pronunciation: the dictionary, then spelling out non-words ──────────────

_TERM_EDGE = "A-Za-z0-9_"


def pronounce(text: str, pronunciations: dict[str, str], spell: bool = True) -> str:
    """Apply the pronunciation dictionary and, with `spell`, spell out non-words.

    Both happen in one pass so that a dictionary replacement is never spelled afterwards.
    Dictionary terms are matched case-insensitively with ASCII edges, like the C#
    (`(?<![A-Za-z0-9_])...(?![A-Za-z0-9_])`); Python's `\\b` would treat CJK as word characters
    and miss the `API` in `中文API`. Longer terms are tried first.
    """
    terms = sorted(((k, v) for k, v in pronunciations.items() if k.strip() and v.strip()),
                   key=lambda kv: len(kv[0]), reverse=True)   # sorted() is stable, like OrderByDescending
    lookup = {k.lower(): v for k, v in terms}
    choices = []
    if terms:
        alternatives = "|".join(re.escape(k) for k, _ in terms)
        choices.append(rf"(?P<term>(?i:(?<![{_TERM_EDGE}])(?:{alternatives})(?![{_TERM_EDGE}])))")
    if spell:
        choices.append(r"(?P<token>[A-Za-z0-9]+)")
    if not choices:
        return text

    def replace(m: re.Match) -> str:
        if m.lastgroup == "term":
            # A function, not a template: a pronunciation containing a backslash is taken literally
            return lookup.get(m.group(0).lower(), m.group(0))
        return spell_out(m.group(0))

    return re.sub("|".join(choices), replace, text)


# Chunks of a compound token: ComfyUI → Comfy|UI, vLLM → v|LLM, XMLHttpRequest → XML|Http|Request,
# k8s → k|8|s, RTX5060 → RTX|5060
_CHUNKS = re.compile(r"[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|\d+")
_PLURAL_ACRONYM = re.compile(r"([A-Z]{2,})s")
# Consonant clusters an English word can start with. Only used to tell an all-caps acronym
# (VRAM, GGUF, SSID) from an all-caps word (NOTE, TODO, YAML, CUDA).
_ONSETS = {"bl", "br", "ch", "cl", "cr", "dr", "dw", "fl", "fr", "gh", "gl", "gn", "gr", "kl", "kn",
           "kr", "ph", "pl", "pr", "ps", "rh", "sc", "sh", "sk", "sl", "sm", "sn", "sp", "sq", "st",
           "sw", "th", "tr", "tw", "wh", "wr", "chr", "phr", "sch", "scr", "shr", "sph", "spl",
           "spr", "str", "thr"}
# Short words with no vowel that are still read as words
_VOWELLESS_WORDS = {"mr", "mrs", "ms", "dr", "st", "vs", "jr", "sr", "mc", "hmm", "shh", "psst", "nth"}
# Names the rules above would spell but every voice already says right. Anything else the user
# wants kept goes in the pronunciation dictionary, mapped to itself.
_KNOWN_WORDS = {"nvidia", "lora", "json"}


def _has_vowel(word: str) -> bool:
    return any(c in "aeiou" for c in word) or "y" in word[1:]


def _pronounceable(word: str) -> bool:
    """Whether an all-caps chunk of four or more letters looks like a word someone would say."""
    if not _has_vowel(word) or re.search(r"[aeiou]{3}", word) or re.match(r"aa|ii|uu", word):
        return False
    onset = re.match(r"[^aeiouy]*", word).group(0)
    return len(onset) < 2 or onset in _ONSETS


def _is_word(chunk: str) -> bool:
    """Whether a voice can read this chunk as a word instead of guessing at it."""
    lower = chunk.lower()
    if chunk.isupper():   # all caps: an acronym, unless it is long and reads like a word
        return len(chunk) >= 4 and _pronounceable(lower)
    return lower in _VOWELLESS_WORDS or _has_vowel(lower)   # lowercase: only vowel-less ones (npm, ssh, llm)


def _letters(chunk: str) -> str:
    return " ".join(chunk.upper())


def spell_out(token: str) -> str:
    """One Latin token (letters and digits) as it should be read.

    Words stay as they are. Acronyms and other non-words become spaced capitals (`G P U`),
    which every voice reads as letters. Compounds are split first, so only the non-word part is
    spelled (`ComfyUI` → `Comfy U I`), and letters and digits are kept apart (`k8s` → `K 8 S`).
    """
    if token.isdecimal() or token.lower() in _KNOWN_WORDS:
        return token
    if m := _PLURAL_ACRONYM.fullmatch(token):   # GPUs, APIs, LLMs (but YAMLs is a word with an s)
        return token if _is_word(m.group(1)) else f"{_letters(m.group(1))} s"
    chunks = _CHUNKS.findall(token)
    spelled = [_letters(c) if len(c) > 1 and not c.isdecimal() and not _is_word(c) else c for c in chunks]
    has_digit = any(c.isdecimal() for c in chunks)
    if spelled == chunks and not has_digit:
        return token   # nothing to spell (PyTorch, GitHub, iPhone): leave it alone
    return " ".join(c.upper() if len(c) == 1 else c for c in spelled)


# ── Sentences and segments: TextSegmenter.Split ─────────────────────────────
#
# Weights: the C# counts UTF-16 units (an emoji is two surrogates, weight 4), this counts code
# points (weight 2). That only moves where a cut lands next to an emoji; the C# FindCut can even
# cut between two surrogates. The difference is accepted.

FIRST_TARGET = 80
LATER_TARGET = 180
_ABBREVIATIONS = {"mr.", "mrs.", "ms.", "dr.", "prof.", "e.g.", "i.e.", "etc.", "vs.", "v."}
_INITIALISM = re.compile(r"^(?:[A-Za-z]\.){2,}$")
_VERSION = re.compile(r"^v?\d+(?:\.\d+)+\.?$", re.I)
_LONG_BREAK = re.compile(r"(?<=[，,、：:])\s*|\s+")


def _char_weight(c: str) -> int:
    return 2 if ord(c) >= 0x2E80 else 0 if c.isspace() else 1


def weight(value: str) -> int:
    return sum(_char_weight(c) for c in value)


def _is_digit(c: str) -> bool:            # char.IsDigit: decimal digits only (str.isdigit accepts ²)
    return c.isdecimal()


def _is_letter_or_digit(c: str) -> bool:  # char.IsLetterOrDigit (str.isalnum accepts ½ and Ⅻ)
    return c.isalpha() or c.isdecimal()


def sentences(text: str):
    start = 0
    n = len(text)
    for i, c in enumerate(text):
        boundary = c in "。！？；\n"
        if c in ".!?":
            token_start = i
            while token_start > start and not text[token_start - 1].isspace():
                token_start -= 1
            token = text[token_start:i + 1]
            decimal_point = 0 < i and i + 1 < n and _is_digit(text[i - 1]) and _is_digit(text[i + 1])
            token_end = i + 1
            while token_end < n and not text[token_end].isspace():
                token_end += 1
            full_token = text[token_start:token_end]
            internal_dot = i + 1 < token_end and _is_letter_or_digit(text[i + 1])
            path_version_or_url = internal_dot and (
                "/" in full_token or "\\" in full_token or "://" in full_token
                or bool(_INITIALISM.match(full_token)) or bool(_VERSION.match(full_token)))
            boundary = not decimal_point and not path_version_or_url and token.lower() not in _ABBREVIATIONS
        if not boundary:
            continue
        value = text[start:i + 1].strip()
        if value:
            yield value
        start = i + 1
    tail = text[start:].strip()
    if tail:
        yield tail


def _find_cut(value: str, target: int) -> int:
    total = 0
    for i, c in enumerate(value):
        total += _char_weight(c)
        if total >= target:
            return i + 1
    return len(value)


def _split_long(text: str, first: bool):
    buffer = ""
    for part in (p for p in _LONG_BREAK.split(text) if p):
        target = FIRST_TARGET if first else LATER_TARGET
        if weight(part) > target:
            if buffer:
                yield buffer.strip()
                buffer, first = "", False
            remaining = part
            while weight(remaining) > target:   # like the C#: the loop keeps the target it came in with
                cut = _find_cut(remaining, FIRST_TARGET if first else LATER_TARGET)
                yield remaining[:cut]
                remaining = remaining[cut:]
                first = False
            buffer += remaining
            continue
        if buffer and weight(buffer) + weight(part) > target:
            yield buffer.strip()
            buffer, first = "", False
        buffer = f"{buffer} {part}" if buffer else part
    if buffer:
        yield buffer.strip()


def split(text: str, first: bool = True) -> list[str]:
    """`TextSegmenter.Split`: the first segment is short so speech starts quickly.

    `first=False` is for text that continues a reading already under way (the next language run).
    """
    if not text.strip():
        return []
    result: list[str] = []
    buffer = ""
    for sentence in sentences(text):
        opening = first and not result
        target = FIRST_TARGET if opening else LATER_TARGET
        if weight(sentence) > target:
            if buffer:
                result.append(buffer.strip())
                buffer = ""
            result.extend(_split_long(sentence, first and not result))
            continue
        if buffer and weight(buffer) + weight(sentence) > target:
            result.append(buffer.strip())
            buffer = ""
        buffer = f"{buffer} {sentence.strip()}" if buffer else sentence.strip()
    if buffer:
        result.append(buffer.strip())
    return result


# ── Languages ───────────────────────────────────────────────────────────────

_LATIN = "A-Za-zÀ-ɏ"
# A stretch of Latin text inside a sentence: from a letter up to the last letter, digit or closing
# mark before the next CJK character. Spaces, digits and ASCII punctuation in between belong to it.
_LATIN_SPAN = re.compile(rf"[{_LATIN}](?:[{_LATIN}0-9 \t'’\-.,!?;:()\"/&+%#@=]*[{_LATIN}0-9.!?)\"'’%])?")
_LATIN_WORD = re.compile(rf"[{_LATIN}]+")


def is_cjk(c: str) -> bool:
    o = ord(c)
    return (0x2E80 <= o <= 0x9FFF or 0xAC00 <= o <= 0xD7AF or 0xF900 <= o <= 0xFAFF
            or 0xFE30 <= o <= 0xFE4F or 0xFF00 <= o <= 0xFFEF or 0x20000 <= o <= 0x3134F)


def language_runs(sentence: str, min_english_words: int) -> list[tuple[str, str]]:
    """Split one sentence into `("zh", text)` and `("en", text)` runs.

    A Latin stretch becomes an English run when it has `min_english_words` words or more, or
    when the sentence has no CJK at all. Anything shorter stays inside the Chinese run around it.
    """
    if not any(is_cjk(c) for c in sentence):
        return [("en", sentence)] if _LATIN_WORD.search(sentence) else [("zh", sentence)]
    runs: list[tuple[str, str]] = []
    pos = 0
    for m in _LATIN_SPAN.finditer(sentence):
        if len(_LATIN_WORD.findall(m.group(0))) < min_english_words:
            continue
        if before := sentence[pos:m.start()].strip():
            runs.append(("zh", before))
        runs.append(("en", m.group(0)))
        pos = m.end()
    if rest := sentence[pos:].strip():
        runs.append(("zh", rest))
    return runs


def segments(text: str, *, voice: str, english_voice: str | None = None, reading_mode: bool = True,
             pronunciations: dict[str, str] | None = None, spell: bool = True,
             min_english_words: int = 3) -> list[dict[str, str]]:
    """The text as a list of `{"text", "voice", "lang"}` segments, in reading order.

    Every segment has one voice. Without `english_voice` this is `split(prepare(...))` -- with
    `spell=False` as well, exactly what the overlay does today.
    """
    terms = DEFAULT_PRONUNCIATIONS if pronunciations is None else pronunciations
    if not english_voice:
        return [{"text": s, "voice": voice, "lang": "zh"}
                for s in split(prepare(text, reading_mode, terms, spell))]

    cleaned = clean(text, reading_mode)
    english_only = not any(is_cjk(c) for c in cleaned)
    groups: list[tuple[str, list[str]]] = []
    for sentence in sentences(cleaned):
        runs = [("en", sentence)] if english_only else language_runs(sentence, min_english_words)
        for lang, run in runs:
            # Which language a run is gets decided on the text as written, before spelling: G P U
            # is three one-letter "words" and must not tip a Chinese sentence into English
            spoken = _normalize(pronounce(run, terms, spell))
            if not any(c.isalnum() for c in spoken):
                continue   # a run that is only punctuation (the 。 after an English run) has nothing to say
            if groups and groups[-1][0] == lang:
                groups[-1][1].append(spoken)
            else:
                groups.append((lang, [spoken]))
    out: list[dict[str, str]] = []
    for lang, pieces in groups:
        for piece in split(" ".join(pieces), first=not out):
            out.append({"text": piece, "voice": english_voice if lang == "en" else voice, "lang": lang})
    return out
