namespace StatusBar.Core.Judgment;

/// <summary>
/// Controls whether the tail of a finished turn's final message is kept in memory for the optional
/// AI check. Off by default: without it the parsers reduce the message to one boolean and discard the
/// text, as D15 requires. Only the last <see cref="MaximumCharacters"/> characters are ever kept, and
/// the text is never logged or persisted.
/// </summary>
public static class FinalMessageCapture
{
    /// <summary>Most characters of a final message that can be held.</summary>
    public const int MaximumCharacters = 1500;

    /// <summary>
    /// Characters of the start of a message that is longer than <see cref="MaximumCharacters"/>. The end of
    /// a long report can read like a question on its own; the start shows it is a deliverable.
    /// </summary>
    public const int StartCharacters = 300;

    static volatile bool _enabled;

    /// <summary>Whether final-message tails are captured.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>The tail of <paramref name="text"/> when capture is on and the text is not blank; otherwise null.</summary>
    internal static string? Tail(string? text)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= MaximumCharacters ? trimmed : trimmed[^MaximumCharacters..];
    }

    /// <summary>The start of <paramref name="text"/> when capture is on and the tail alone does not cover it; otherwise null.</summary>
    internal static string? Start(string? text)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= MaximumCharacters ? null : trimmed[..StartCharacters];
    }
}
