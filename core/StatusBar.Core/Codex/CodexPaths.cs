namespace StatusBar.Core.Codex;

/// <summary>Resolved Codex home and its task-discovery paths.</summary>
internal sealed record CodexPaths(string Home, string SessionsDirectory, string SessionIndexPath)
{
    internal static CodexPaths Resolve(
        string? overrideHome = null,
        string? environmentCodexHome = null,
        string? userProfile = null)
    {
        var home = FirstNonBlank(overrideHome, environmentCodexHome);
        if (home is null)
        {
            var profile = FirstNonBlank(userProfile, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            home = Path.Combine(profile ?? ".", ".codex");
        }

        return new CodexPaths(
            home,
            Path.Combine(home, "sessions"),
            Path.Combine(home, "session_index.jsonl"));
    }

    static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
