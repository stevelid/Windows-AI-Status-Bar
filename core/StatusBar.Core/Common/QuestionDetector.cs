namespace StatusBar.Core.Common;

/// <summary>Detects whether the final non-empty line of a message ends with a question mark.</summary>
public static class QuestionDetector
{
    const string ClosingCharacters = "*_`)\"'”’";

    /// <summary>Returns whether the final non-empty line ends in <c>?</c> or the full-width equivalent.</summary>
    public static bool EndsWithQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var lines = text.Split('\n');
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = lines[index].TrimEnd();
            if (line.Length == 0) continue;

            line = line.TrimEnd(ClosingCharacters.ToCharArray());
            return line.EndsWith('?') || line.EndsWith('？');
        }

        return false;
    }
}
