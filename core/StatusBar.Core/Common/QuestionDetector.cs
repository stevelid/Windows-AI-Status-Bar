using System.Text.RegularExpressions;

namespace StatusBar.Core.Common;

/// <summary>
/// Decides whether a final assistant message ends by asking Steve something (design change D15).
/// </summary>
/// <remarks>
/// Assistants rarely put the question mark on the very last line. They often follow the question
/// with a list of options ("Which format?" then "1. PDF" / "2. Word") or a short closing
/// sentence ("Should I continue? Let me know."). So the check skips trailing option lines and
/// then looks for a question mark anywhere in the final paragraph. The result is Inferred and
/// dismissable; the text is never stored.
/// </remarks>
public static partial class QuestionDetector
{
    const string ClosingCharacters = "*_`)\"'”’";
    const int MaxTrailingOptionLines = 12;

    /// <summary>Returns whether the message's final paragraph asks a question.</summary>
    public static bool EndsWithQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var lines = text.Replace("\r", "").Split('\n');
        var index = lines.Length - 1;

        // Skip trailing blank lines and up to a dozen option-list lines ("1. PDF", "- Word", "a) CSV").
        var skippedOptions = 0;
        while (index >= 0)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) { index--; continue; }
            if (skippedOptions < MaxTrailingOptionLines && OptionLine().IsMatch(line))
            {
                skippedOptions++;
                index--;
                continue;
            }
            break;
        }

        // Scan the final paragraph (back to the previous blank line) for a question mark.
        for (; index >= 0; index--)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) break;
            line = line.TrimEnd(ClosingCharacters.ToCharArray());
            if (line.Contains('?') || line.Contains('？')) return true;
        }

        return false;
    }

    // A list item: bullet, "1." / "1)", "a." / "a)" or "(a)", followed by text.
    [GeneratedRegex(@"^([-*•]|\d{1,2}[.)]|[A-Za-z][.)]|\([A-Za-z0-9]\))\s+\S")]
    private static partial Regex OptionLine();
}
