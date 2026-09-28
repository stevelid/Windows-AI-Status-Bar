using StatusBar.Core.Tasks;

namespace ClaudeUsageWidget;

/// <summary>Turns a newly gated attention task into a tray balloon.</summary>
internal sealed class AttentionNotifier
{
    readonly Settings _settings;
    readonly TrayController _tray;

    public AttentionNotifier(Settings settings, TrayController tray)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tray);
        _settings = settings;
        _tray = tray;
    }

    /// <summary>Shows one notification when attention notifications are enabled.</summary>
    public void Notify(AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_settings.NotificationsEnabled) return;

        var provider = task.Provider == AgentProvider.Codex
            ? L10n.T("pane_provider_codex")
            : L10n.T("pane_provider_claude");
        _tray.ShowAttention($"{provider} {L10n.T("notification_needs_you")}", task.Title);
    }
}
