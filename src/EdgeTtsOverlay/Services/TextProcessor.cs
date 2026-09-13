using System.Text.RegularExpressions;

namespace EdgeTtsOverlay.Services;

public static partial class TextProcessor
{
    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.Multiline)]
    private static partial Regex CodeBlockRegex();
    [GeneratedRegex(@"\[([^\]]+)\]\(https?://[^\s)]+\)")]
    private static partial Regex MarkdownLinkRegex();
    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlRegex();
    [GeneratedRegex(@"(?<!\w)(?:[A-Za-z]:\\|/)(?:[^\s<>:\""|?*]+[/\\])*([^\s/\\<>:\""|?*]+)")]
    private static partial Regex PathRegex();
    [GeneratedRegex(@"(?m)^\s{0,3}(?:#{1,6}\s*|[-*+]\s+|\d+[.)]\s+|>\s*)")]
    private static partial Regex MarkdownPrefixRegex();
    [GeneratedRegex(@"[*_~]{1,3}")]
    private static partial Regex MarkdownDecorationRegex();
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpacesRegex();
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex NewlinesRegex();

    public static string Prepare(string input, bool readingMode, IReadOnlyDictionary<string, string> pronunciations)
    {
        var text = input.Replace("\r\n", "\n").Trim();
        if (readingMode)
        {
            text = CodeBlockRegex().Replace(text, "\n程式碼區塊略過。\n");
            text = MarkdownLinkRegex().Replace(text, "$1");
            text = UrlRegex().Replace(text, "網址略過");
            text = PathRegex().Replace(text, "$1");
            text = MarkdownPrefixRegex().Replace(text, string.Empty);
            text = MarkdownDecorationRegex().Replace(text, string.Empty);
            text = text.Replace("`", string.Empty);
        }

        var terms = pronunciations.Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
            .OrderByDescending(x => x.Key.Length).ToArray();
        if (terms.Length > 0)
        {
            var lookup = terms.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            var alternatives = string.Join("|", terms.Select(x => Regex.Escape(x.Key)));
            text = Regex.Replace(text, $@"(?<![A-Za-z0-9_])(?:{alternatives})(?![A-Za-z0-9_])",
                match => lookup[match.Value], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        text = SpacesRegex().Replace(text, " ");
        return NewlinesRegex().Replace(text, "\n\n").Trim();
    }
}
