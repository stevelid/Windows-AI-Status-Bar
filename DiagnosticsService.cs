using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using StatusBar.Core.Codex;
using StatusBar.Core.Usage;

namespace ClaudeUsageWidget;

public sealed record ProviderDiagnostic(
    UsageSource Provider,
    UsageHealth Health,
    DateTimeOffset? LastSuccess,
    string Status);

public sealed record CodexDiagnostic(
    string Source,
    string ExecutableName,
    string Version);

/// <summary>
/// Builds an intentionally redacted support report from provider status projections.
/// It never outputs credentials, individual usage values, account identifiers, log contents, or full paths.
/// </summary>
public static class DiagnosticsService
{
    public static string BuildReport(
        Version appVersion,
        IEnumerable<ProviderDiagnostic> providers,
        CodexDiagnostic codex,
        CodexTaskDiagnostics? codexTasks = null)
    {
        var report = new StringBuilder();
        report.AppendLine("AI Usage Widget diagnostics (redacted)");
        report.AppendLine($"AppVersion: {NormalizeVersion(appVersion)}");
        report.AppendLine($"Build: {AppBuild.Label}");
        report.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        report.AppendLine($"Architecture: {RuntimeInformation.OSArchitecture}");
        foreach (var item in providers.OrderBy(item => item.Provider))
        {
            var prefix = item.Provider switch
            {
                UsageSource.Claude => "Claude",
                UsageSource.Codex => "ChatGPT",
                _ => "Unknown",
            };
            report.AppendLine($"{prefix}.LastSuccess: {FormatTimestamp(item.LastSuccess)}");
            report.AppendLine($"{prefix}.Health: {item.Health}");
            report.AppendLine($"{prefix}.Status: {item.Status}");
        }
        report.AppendLine($"Codex.Source: {codex.Source}");
        report.AppendLine($"Codex.Executable: {codex.ExecutableName}");
        report.AppendLine($"Codex.Version: {codex.Version}");
        AppendCodexTaskDiagnostics(report, codexTasks);
        report.AppendLine("Privacy: no tokens, account data, usage values, log contents, or full paths included.");
        return report.ToString();
    }

    static void AppendCodexTaskDiagnostics(StringBuilder report, CodexTaskDiagnostics? diagnostics)
    {
        if (diagnostics is null)
        {
            report.AppendLine("CodexTasks.Health: NotConfigured");
            return;
        }

        report.AppendLine($"CodexTasks.Health: {diagnostics.Health.State}/{SanitizeStatusCode(diagnostics.Health.Code)}");
        report.AppendLine($"CodexTasks.SessionsTracked: {Math.Max(0, diagnostics.SessionsTracked)}");
        report.AppendLine($"CodexTasks.FilesWatched: {Math.Max(0, diagnostics.FilesWatched)}");
        report.AppendLine($"CodexTasks.LastEventAge: {FormatAge(diagnostics.LastEventAge)}");
        report.AppendLine($"CodexTasks.ParseErrors: {Math.Max(0, diagnostics.ParseErrors)}");
        report.AppendLine($"CodexTasks.FormatDriftCount: {Math.Max(0, diagnostics.FormatDriftCount)}");
        report.AppendLine($"CodexTasks.WatcherOverflowCount: {Math.Max(0, diagnostics.WatcherOverflowCount)}");
        if (diagnostics.FormatDriftBySignature.Count == 0)
        {
            report.AppendLine("CodexTasks.FormatDriftBySignature: none");
            return;
        }

        foreach (var (signature, count) in diagnostics.FormatDriftBySignature.OrderBy(item => item.Key, StringComparer.Ordinal))
            report.AppendLine($"CodexTasks.FormatDrift.{SanitizeSignature(signature)}: {Math.Max(0, count)}");
    }

    static string SanitizeStatusCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        var safe = new string(value.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return safe.Length is > 0 and <= 48 ? safe : "Unknown";
    }

    static string SanitizeSignature(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "other";
        var components = value.Split('/');
        if (components.Length is 0 or > 2) return "other";
        return string.Join('/', components.Select(component =>
            component.Length is > 0 and <= 48 && char.IsAsciiLetter(component[0]) &&
            component.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')
                ? component
                : "other"));
    }

    static string FormatAge(TimeSpan? age)
    {
        if (age is null) return "Never";
        var seconds = Math.Max(0, (long)age.Value.TotalSeconds);
        return $"{seconds}s";
    }

    public static string ClassifyError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "AuthenticationRequired",
        HttpRequestException => "NetworkUnavailable",
        CodexUnavailableException => "CodexUnavailable",
        OperationCanceledException => "Cancelled",
        _ when exception.Message.Contains("429", StringComparison.OrdinalIgnoreCase) => "RateLimited",
        _ => $"Error:{exception.GetType().Name}",
    };

    public static CodexDiagnostic InspectCodex(string? configuredPath)
    {
        try
        {
            var resolved = CodexLocator.Resolve(configuredPath);
            var source = !string.IsNullOrWhiteSpace(configuredPath)
                ? "Configured"
                : IsChatGptDesktopCodex(resolved) ? "ChatGPTDesktop" : "Automatic";
            var executableName = Path.GetFileName(resolved);
            if (string.IsNullOrWhiteSpace(executableName)) executableName = "unknown";

            var version = "unknown";
            if (Path.IsPathFullyQualified(resolved) && File.Exists(resolved))
            {
                var rawVersion = FileVersionInfo.GetVersionInfo(resolved).FileVersion;
                version = SanitizeVersion(rawVersion);
            }

            return new CodexDiagnostic(source, executableName, version);
        }
        catch (Exception ex)
        {
            return new CodexDiagnostic("Unavailable", "unknown", ClassifyError(ex));
        }
    }

    static bool IsChatGptDesktopCodex(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}OpenAI{Path.DirectorySeparatorChar}Codex{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);

    static string SanitizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "unknown";
        var safe = new string(version.Where(ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '+' or '_').ToArray());
        return safe.Length is > 0 and <= 80 ? safe : "unknown";
    }

    static string NormalizeVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "Never";
}
