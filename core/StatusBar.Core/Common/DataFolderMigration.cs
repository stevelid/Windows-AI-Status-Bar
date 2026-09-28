namespace StatusBar.Core.Common;

/// <summary>A file that should be copied as part of a data-folder migration.</summary>
public sealed record DataFolderFileCopy(string SourcePath, string DestinationPath);

/// <summary>Plans the small, safe set of files imported from the legacy app folder.</summary>
public static class DataFolderMigration
{
    /// <summary>The marker written after a legacy token import has been handled.</summary>
    public const string ImportCompletedMarkerFileName = ".legacy-token-imported";

    /// <summary>
    /// Returns the legacy Claude token file when it exists, the destination does not,
    /// and the import has not already been completed. Settings are intentionally
    /// excluded because the new app has a different layout.
    /// </summary>
    public static IReadOnlyList<DataFolderFileCopy> Plan(
        string oldDir,
        string newDir,
        Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(newDir);
        ArgumentNullException.ThrowIfNull(exists);

        if (exists(Path.Combine(newDir, ImportCompletedMarkerFileName))) return Array.Empty<DataFolderFileCopy>();

        var source = Path.Combine(oldDir, "tokens.dat");
        var destination = Path.Combine(newDir, "tokens.dat");
        if (!exists(source) || exists(destination)) return Array.Empty<DataFolderFileCopy>();

        return [new DataFolderFileCopy(source, destination)];
    }
}
