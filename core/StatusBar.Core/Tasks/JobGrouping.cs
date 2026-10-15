using System.Text.RegularExpressions;

namespace StatusBar.Core.Tasks;

/// <summary>A run of conversations shown together; <see cref="JobNumber"/> is null for a lone conversation.</summary>
public sealed record TaskCluster(string? JobNumber, IReadOnlyList<AgentTask> Tasks);

/// <summary>
/// Groups conversations that belong to the same Venta job, using only the title. Pure and in-memory:
/// nothing is persisted, synced or sent to a model.
/// </summary>
public static partial class JobGrouping
{
    // Venta job numbers are 4 digits. The lookarounds reject digits, letters, "_" and a "." or "," that
    // joins the token to another digit (so "12345", "3.0.1234" and "v1.2024" never match) while still
    // allowing a suffix such as "5863.L1" or a trailing sentence full stop.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(?<![0-9][.,])[0-9]{4}(?![\p{L}\p{N}_])(?![.,][0-9])")]
    private static partial Regex JobNumberPattern();

    /// <summary>
    /// The first standalone 4-digit token in <paramref name="title"/>, or null. Years 1900-2099 are skipped
    /// so a title such as "2026 plan" is not mistaken for a job.
    /// </summary>
    public static string? JobNumber(string title)
    {
        foreach (Match match in JobNumberPattern().Matches(title))
        {
            var value = int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (value is >= 1900 and <= 2099) continue;
            return match.Value;
        }

        return null;
    }

    /// <summary>
    /// Keeps the incoming order, but pulls conversations that share a job number (two or more) into one
    /// cluster at the position of the first member. Everything else is its own cluster with no job number.
    /// </summary>
    public static IReadOnlyList<TaskCluster> Cluster(IReadOnlyList<AgentTask> tasks)
    {
        var jobs = tasks.Select(task => JobNumber(task.Title)).ToArray();
        var shared = jobs.Where(job => job is not null).GroupBy(job => job).Where(g => g.Count() > 1)
            .Select(g => g.Key!).ToHashSet(StringComparer.Ordinal);
        var clusters = new List<TaskCluster>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tasks.Count; i++)
        {
            var job = jobs[i];
            if (job is null || !shared.Contains(job))
            {
                clusters.Add(new TaskCluster(null, [tasks[i]]));
            }
            else if (placed.Add(job))
            {
                var members = tasks.Where((_, index) => jobs[index] == job).ToArray();
                clusters.Add(new TaskCluster(job, members));
            }
        }

        return clusters;
    }
}
