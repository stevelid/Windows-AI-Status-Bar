using System.Text;
using StatusBar.Core.Codex;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Diagnostics;

/// <summary>Content-free usage-provider status supplied to the diagnostic report.</summary>
public sealed record DiagnosticUsageProvider(
    string Name,
    string Health,
    DateTimeOffset? LastSuccess,
    string Status);

/// <summary>Builds the redacted report and format-drift text used by support bundles.</summary>
public static class DiagnosticReportBuilder
{
    /// <summary>
    /// Builds a report from status projections and counts. Task titles, IDs and session
    /// references are deliberately read only for counting and are never formatted.
    /// </summary>
    public static string Build(
        string appVersion,
        string buildLabel,
        IEnumerable<DiagnosticUsageProvider> providers,
        StatusBarState taskState,
        CodexTaskDiagnostics? codexTasks,
        string codexSource,
        string codexExecutable,
        string codexVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildLabel);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(taskState);
        ArgumentNullException.ThrowIfNull(codexSource);
        ArgumentNullException.ThrowIfNull(codexExecutable);
        ArgumentNullException.ThrowIfNull(codexVersion);

        var report = new StringBuilder();
        report.AppendLine("Windows AI Status Bar diagnostics (redacted)");
        report.AppendLine($"AppVersion: {SafeValue(appVersion, 80)}");
        report.AppendLine($"Build: {SafeValue(buildLabel, 80)}");

        foreach (var provider in providers.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(provider);
            var prefix = SafeKey(provider.Name, "Unknown");
            report.AppendLine($"Provider.{prefix}.LastSuccess: {FormatTimestamp(provider.LastSuccess)}");
            report.AppendLine($"Provider.{prefix}.Health: {SafeKey(provider.Health, "Unknown")}");
            report.AppendLine($"Provider.{prefix}.Status: {SafeKey(provider.Status, "Unknown")}");
        }

        AppendTaskCounts(report, taskState);
        AppendTaskProviderHealth(report, taskState);
        report.AppendLine($"Codex.Source: {SafeKey(codexSource, "Unknown")}");
        report.AppendLine($"Codex.Executable: {SafeFileName(codexExecutable)}");
        report.AppendLine($"Codex.Version: {SafeValue(codexVersion, 80)}");
        AppendCodexDiagnostics(report, codexTasks);
        report.AppendLine("Privacy: no task titles, prompts, IDs, session references, tokens, account data, log contents, or full paths included.");
        return report.ToString();
    }

    /// <summary>Builds the separate parser drift entry without retaining record content.</summary>
    public static string BuildFormatDrift(CodexTaskDiagnostics? diagnostics)
    {
        var report = new StringBuilder();
        report.AppendLine("Parser format drift (redacted)");
        if (diagnostics is null)
        {
            report.AppendLine("Codex: NotConfigured");
            return report.ToString();
        }

        report.AppendLine($"Codex.ParseErrors: {Math.Max(0, diagnostics.ParseErrors)}");
        report.AppendLine($"Codex.FormatDriftCount: {Math.Max(0, diagnostics.FormatDriftCount)}");
        report.AppendLine($"Codex.WatcherOverflowCount: {Math.Max(0, diagnostics.WatcherOverflowCount)}");
        if (diagnostics.FormatDriftBySignature.Count == 0)
        {
            report.AppendLine("Codex.Signatures: none");
            return report.ToString();
        }

        foreach (var (signature, count) in diagnostics.FormatDriftBySignature.OrderBy(item => item.Key, StringComparer.Ordinal))
            report.AppendLine($"Codex.Signature.{SanitizeSignature(signature)}: {Math.Max(0, count)}");
        return report.ToString();
    }

    static void AppendTaskCounts(StringBuilder report, StatusBarState state)
    {
        report.AppendLine($"Tasks.Total: {state.Tasks.Count}");
        foreach (var status in Enum.GetValues<AgentTaskStatus>())
        {
            var count = state.Tasks.Count(task => task.Status == status);
            report.AppendLine($"Tasks.{status}: {count}");
        }
        report.AppendLine($"Tasks.WorkingReported: {Math.Max(0, state.WorkingCount)}");
        report.AppendLine($"Tasks.AttentionReported: {Math.Max(0, state.AttentionCount)}");
    }

    static void AppendTaskProviderHealth(StringBuilder report, StatusBarState state)
    {
        foreach (var provider in Enum.GetValues<AgentProvider>())
        {
            var prefix = provider.ToString();
            var count = state.Tasks.Count(task => task.Provider == provider);
            report.AppendLine($"TaskProvider.{prefix}.Count: {count}");
            if (!state.TaskProviderHealth.TryGetValue(provider, out var health))
            {
                report.AppendLine($"TaskProvider.{prefix}.Health: NotConfigured");
                continue;
            }

            report.AppendLine($"TaskProvider.{prefix}.Health: {health.State}");
            report.AppendLine($"TaskProvider.{prefix}.Code: {SafeKey(health.Code, "Unknown")}");
            report.AppendLine($"TaskProvider.{prefix}.LastEvidence: {(health.LastEvidence is null ? "Never" : "Present")}");
        }
    }

    static void AppendCodexDiagnostics(StringBuilder report, CodexTaskDiagnostics? diagnostics)
    {
        if (diagnostics is null)
        {
            report.AppendLine("CodexTasks.Health: NotConfigured");
            return;
        }

        report.AppendLine($"CodexTasks.Health: {diagnostics.Health.State}/{SafeKey(diagnostics.Health.Code, "Unknown")}");
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

    static string SafeKey(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var safe = new string(value.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return safe.Length is > 0 and <= 80 ? safe : fallback;
    }

    static string SafeValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        var safe = new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return safe.Length > 0 && safe.Length <= maxLength ? safe : "Unknown";
    }

    static string SafeFileName(string? value)
    {
        var name = Path.GetFileName(value);
        return string.IsNullOrWhiteSpace(name) ? "unknown" : SafeValue(name, 80);
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

    static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "Never";
}
