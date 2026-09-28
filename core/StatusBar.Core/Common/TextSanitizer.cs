using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StatusBar.Core.Common;

/// <summary>Reduces provider-supplied text to a short, display-safe task title.</summary>
internal static class TextSanitizer
{
    const int MaxTitleElements = 48;
    static readonly Regex MarkdownLink = new(@"!?\[([^\]]+)\]\([^)]+\)", RegexOptions.CultureInvariant);
    static readonly Regex MarkdownMarkers = new(@"\*\*|__|~~|[`*_]", RegexOptions.CultureInvariant);

    /// <summary>Returns the first useful line, or <see langword="null"/> for injected or empty content.</summary>
    internal static string? SanitizeTitleCandidate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var firstLine = text.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
        if (firstLine is null || IsInjectedContext(firstLine)) return null;

        var normalized = MarkdownLink.Replace(firstLine, "$1");
        normalized = MarkdownMarkers.Replace(normalized, string.Empty).TrimStart('#', '>', '+', '-');
        normalized = CollapseWhitespace(normalized).Trim();
        if (normalized.Length == 0) return null;
        return TruncateAtWordBoundary(normalized);
    }

    static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static string TruncateAtWordBoundary(string text)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        if (elements.Count <= MaxTitleElements) return text;

        var keep = MaxTitleElements - 1;
        var boundary = elements.Take(keep).ToList().FindLastIndex(element => element.All(char.IsWhiteSpace));
        if (boundary > 0) keep = boundary;
        return string.Concat(elements.Take(keep)).TrimEnd() + "…";
    }

    static bool IsInjectedContext(string line) =>
        line.StartsWith("<environment_context>", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("<user_instructions>", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("<permissions", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("<system-reminder>", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("<developer_instructions>", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("# AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("# CLAUDE.md", StringComparison.OrdinalIgnoreCase);
}
