using System.Text.RegularExpressions;

namespace StatusBar.Core.Projects;

/// <summary>Removes common credentials, private paths and injected context wrappers before a summary call.</summary>
public static partial class ProjectSummaryTextSanitizer
{
    [GeneratedRegex(@"(?is)<(?:system-reminder|environment_context|developer_message|task-notification|local-command-caveat|app-context|skills_instructions|user-instructions)>.*?</(?:system-reminder|environment_context|developer_message|task-notification|local-command-caveat|app-context|skills_instructions|user-instructions)>", RegexOptions.CultureInvariant)]
    private static partial Regex ContextBlock();
    [GeneratedRegex(@"(?i)\b(?:sk-[A-Za-z0-9_-]{12,}|gh[pousr]_[A-Za-z0-9]{20,}|Bearer\s+[A-Za-z0-9._~+/-]{12,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex Credential();
    [GeneratedRegex(@"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Email();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Z]:\\|\\\\)[^\s<>""']{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();
    [GeneratedRegex(@"(?<![A-Za-z0-9])/(?:home|Users|mnt|tmp)/[^\s<>""']{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPath();

    public static string? Sanitize(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value) || maximumCharacters <= 0) return null;
        var text = ContextBlock().Replace(value, " ");
        text = Credential().Replace(text, "[credential]");
        text = Email().Replace(text, "[email]");
        text = WindowsPath().Replace(text, "[local path]");
        text = UnixPath().Replace(text, "[local path]");
        text = new string(text.Where(character => !char.IsControl(character) || character is '\n' or '\t').ToArray()).Trim();
        if (text.Length > maximumCharacters) text = text[^maximumCharacters..];
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
