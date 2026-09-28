namespace StatusBar.Core.Claude;

/// <summary>Resolved Claude Code home and transcript discovery directory.</summary>
internal sealed record ClaudeCodePaths(string Home, string ProjectsDirectory)
{
    internal static ClaudeCodePaths Resolve(
        string? overrideHome = null,
        string? environmentConfigDir = null,
        string? userProfile = null)
    {
        var home = FirstNonBlank(overrideHome, environmentConfigDir);
        if (home is null)
        {
            // ⚠️ A-K1 The default Claude home is confirmed for desktop Code-tab sessions only.
            var profile = FirstNonBlank(userProfile, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            home = Path.Combine(profile ?? ".", ".claude");
        }

        return new ClaudeCodePaths(home, Path.Combine(home, "projects"));
    }

    static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
