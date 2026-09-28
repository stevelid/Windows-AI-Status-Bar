using System.Reflection;

namespace ClaudeUsageWidget;

/// <summary>
/// Identifies the exact build, e.g. "3.0.0+a1b2c3d". The .NET SDK appends the git commit to the
/// informational version, which lets Steve and the agents tell CI downloads apart in logs and
/// diagnostics.
/// </summary>
public static class AppBuild
{
    public static string Label { get; } = Resolve();

    static string Resolve()
    {
        var info = typeof(AppBuild).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            return typeof(AppBuild).Assembly.GetName().Version?.ToString() ?? "unknown";

        // Shorten "3.0.0+<40-char sha>" to "3.0.0+<7-char sha>".
        var plus = info.IndexOf('+');
        return plus >= 0 && info.Length > plus + 8 ? info[..(plus + 8)] : info;
    }
}
