using System.Text;
using System.Text.RegularExpressions;

namespace EdgeTtsOverlay.Services;

public static class TextSegmenter
{
    private const int FirstTargetWeight = 80;
    private const int LaterTargetWeight = 180;
    private const int HardWeight = 240;
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
        { "Mr.", "Mrs.", "Ms.", "Dr.", "Prof.", "e.g.", "i.e.", "etc.", "vs.", "v." };

    public static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var sentences = SplitSentences(text);
        var result = new List<string>();
        var buffer = new StringBuilder();

        foreach (var sentence in sentences)
        {
            var target = result.Count == 0 ? FirstTargetWeight : LaterTargetWeight;
            if (Weight(sentence) > target)
            {
                Flush(buffer, result);
                result.AddRange(SplitLong(sentence, result.Count == 0));
                continue;
            }

            if (buffer.Length > 0 && Weight(buffer.ToString()) + Weight(sentence) > target)
                Flush(buffer, result);
            if (buffer.Length > 0) buffer.Append(' ');
            buffer.Append(sentence.Trim());
        }
        Flush(buffer, result);
        return result;
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var boundary = c is '。' or '！' or '？' or '；' or '\n';
            if (c is '.' or '!' or '?')
            {
                var tokenStart = i;
                while (tokenStart > start && !char.IsWhiteSpace(text[tokenStart - 1])) tokenStart--;
                var token = text[tokenStart..(i + 1)];
                var decimalPoint = i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]);
                var tokenEnd = i + 1;
                while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd])) tokenEnd++;
                var fullToken = text[tokenStart..tokenEnd];
                var internalDot = i + 1 < tokenEnd && char.IsLetterOrDigit(text[i + 1]);
                var pathVersionOrUrl = internalDot && (fullToken.Contains('/') || fullToken.Contains('\\')
                    || fullToken.Contains("://", StringComparison.Ordinal)
                    || Regex.IsMatch(fullToken, @"^(?:[A-Za-z]\.){2,}$")
                    || Regex.IsMatch(fullToken, @"^v?\d+(?:\.\d+)+\.?$", RegexOptions.IgnoreCase));
                boundary = !decimalPoint && !pathVersionOrUrl && !Abbreviations.Contains(token);
            }
            if (!boundary) continue;
            var value = text[start..(i + 1)].Trim();
            if (value.Length > 0) yield return value;
            start = i + 1;
        }
        var tail = text[start..].Trim();
        if (tail.Length > 0) yield return tail;
    }

    private static IEnumerable<string> SplitLong(string text, bool first)
    {
        var parts = Regex.Split(text, @"(?<=[，,、：:])\s*|\s+");
        var buffer = new StringBuilder();
        foreach (var part in parts.Where(p => p.Length > 0))
        {
            var target = first ? FirstTargetWeight : LaterTargetWeight;
            if (Weight(part) > target)
            {
                if (buffer.Length > 0) { yield return buffer.ToString().Trim(); buffer.Clear(); first = false; }
                var remaining = part;
                while (Weight(remaining) > target)
                {
                    var cut = FindCut(remaining, first ? FirstTargetWeight : LaterTargetWeight);
                    yield return remaining[..cut];
                    remaining = remaining[cut..];
                    first = false;
                }
                if (remaining.Length > 0) buffer.Append(remaining);
                continue;
            }
            if (buffer.Length > 0 && Weight(buffer.ToString()) + Weight(part) > target)
            {
                yield return buffer.ToString().Trim();
                buffer.Clear();
                first = false;
            }
            if (buffer.Length > 0) buffer.Append(' ');
            buffer.Append(part);
        }
        if (buffer.Length > 0) yield return buffer.ToString().Trim();
    }

    private static int FindCut(string value, int target)
    {
        var weight = 0;
        for (var i = 0; i < value.Length; i++)
        {
            weight += value[i] >= 0x2E80 ? 2 : char.IsWhiteSpace(value[i]) ? 0 : 1;
            if (weight >= target) return i + 1;
        }
        return value.Length;
    }

    private static int Weight(string value) => value.Sum(c => c >= 0x2E80 ? 2 : char.IsWhiteSpace(c) ? 0 : 1);
    private static void Flush(StringBuilder buffer, List<string> result)
    {
        if (buffer.Length == 0) return;
        result.Add(buffer.ToString().Trim());
        buffer.Clear();
    }
}
