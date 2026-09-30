using StatusBar.Core.Judgment;

namespace StatusBar.Core.Tests.Judgment;

/// <summary>
/// Runs the real classifier against the live TypeSafe API on made-up messages of the shapes seen in use.
/// It does nothing unless the JEV_AI_API_KEY environment variable is set, so CI and other machines skip it.
/// Run it after changing the wording in <see cref="JevTurnEndClassifier"/> (and bump its PromptVersion).
/// </summary>
public sealed class JevLiveTests
{
    // (should the user be alerted, made-up message). None of this is real content.
    static readonly (bool Alert, string Text)[] Messages =
    [
        (true, "I found two ways to migrate the settings file. Option A keeps the old keys and adds the new ones. Option B rewrites everything to the new schema, but existing users lose custom values unless we add a converter. Which option would you like me to take?"),
        (true, "The cleanup needs to delete the 14 old log folders under archive/. I haven't touched them yet. Do you want me to go ahead and delete them?"),
        (true, "I couldn't finish: the build server rejected my credentials (401) and I have no other way to authenticate. I need a valid token from you before I can push or run the pipeline."),
        (true, "Here is the plan: 1) split the parser into two classes, 2) add tests for the edge cases, 3) update the docs. Let me know if you'd like me to start, or change anything first."),
        (true, "I've prepared the commit with the message 'Fix locale cache key'. Want me to push it to the remote?"),
        (true, "Which file should I edit?"),
        (true, "The analysis is finished and the numbers are in the table above. I noticed the Q3 export contains duplicate order IDs. I can't tell whether to drop or keep them, and it changes the totals. Which should I do?"),
        (false, "Things to sort before it goes in the summary\n\n1. The new tolerance rows are a fresh assumption; earlier we agreed to allow none until the geometry was confirmed.\n2. The short-span test shows only a 16 reduction. It isn't governing, but is worth one line.\n3. The measured values stop at 2 k; the extended values need a source.\n\nNext steps\n\n* Send me the geometry, or point me to the workbook, and I can run the check and write the assumptions paragraph.\n* Then I can redraft the report short."),
        (false, "All 42 tests pass and the release build is clean. I've bumped the version to 2.4.0. Want me to also draft the release notes?"),
        (false, "Finished. The report is saved as summary.docx, six pages, with the three charts embedded. No open issues."),
        (false, "The refactor is complete. Two things you may want to consider later: the config loader could be cached, and the logging format is inconsistent between modules. Neither is urgent."),
        (false, "I've drafted the section using placeholder figures. When you have the final measurements, send them over and I'll swap them in. Everything else is complete."),
        (false, "I've updated the three config files and re-run the checks; everything passes. Let me know if you want any changes."),
        (false, "I'll go with option A (keep the old keys) since it's the least risky, and I'm starting on it now. Shout if you'd rather I did B instead."),
        (false, "Three tests still fail (listed above). I've narrowed them to the date parser and I'm continuing with that next; no action needed from you."),
    ];

    [Fact]
    public async Task The_shipped_wording_alerts_only_on_a_stopped_assistant()
    {
        var key = Environment.GetEnvironmentVariable("JEV_AI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var classifier = new JevTurnEndClassifier(http, key);
        var wrong = new List<string>();
        foreach (var (alert, text) in Messages)
        {
            var judgment = await classifier.ClassifyAsync(text, CancellationToken.None);
            var alerted = judgment.Alert >= TurnVerdictPolicy.AskThreshold;
            if (alerted != alert) wrong.Add($"expected {(alert ? "alert" : "quiet")} but alert={judgment.Alert:0.00} kind={judgment.Kind}: {text[..Math.Min(50, text.Length)]}");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }
}
