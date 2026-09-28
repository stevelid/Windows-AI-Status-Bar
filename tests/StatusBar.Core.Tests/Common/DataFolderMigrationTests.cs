using StatusBar.Core.Common;

namespace StatusBar.Core.Tests.Common;

public class DataFolderMigrationTests
{
    [Fact]
    public void Plans_token_copy_when_source_exists_and_destination_is_missing()
    {
        var oldDir = Path.Combine("old", "app");
        var newDir = Path.Combine("new", "app");
        var source = Path.Combine(oldDir, "tokens.dat");
        var destination = Path.Combine(newDir, "tokens.dat");

        var plan = DataFolderMigration.Plan(oldDir, newDir, path => path == source);

        Assert.Equal([new DataFolderFileCopy(source, destination)], plan);
    }

    [Fact]
    public void Does_not_overwrite_existing_destination_token()
    {
        var oldDir = Path.Combine("old", "app");
        var newDir = Path.Combine("new", "app");
        var source = Path.Combine(oldDir, "tokens.dat");
        var destination = Path.Combine(newDir, "tokens.dat");

        var plan = DataFolderMigration.Plan(
            oldDir,
            newDir,
            path => path == source || path == destination);

        Assert.Empty(plan);
    }

    [Fact]
    public void Does_not_import_again_after_migration_marker_exists()
    {
        var oldDir = Path.Combine("old", "app");
        var newDir = Path.Combine("new", "app");
        var source = Path.Combine(oldDir, "tokens.dat");
        var marker = Path.Combine(newDir, DataFolderMigration.ImportCompletedMarkerFileName);

        var plan = DataFolderMigration.Plan(
            oldDir,
            newDir,
            path => path == source || path == marker);

        Assert.Empty(plan);
    }

    [Fact]
    public void Does_nothing_when_legacy_folder_has_no_token_file()
    {
        var plan = DataFolderMigration.Plan("old", "new", _ => false);

        Assert.Empty(plan);
    }
}
