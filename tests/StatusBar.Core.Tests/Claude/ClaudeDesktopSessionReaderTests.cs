using StatusBar.Core.Claude;

namespace StatusBar.Core.Tests.Claude;

public sealed class ClaudeDesktopSessionReaderTests
{
    const string CliSession = "00000000-0000-4000-8000-000000000001";

    [Fact]
    public void Resolves_desktop_identity_across_roots_skipping_bad_archived_and_unrelated_metadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "statusbar-navigation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = Path.Combine(root, "ordinary");
            var second = Path.Combine(root, "package");
            var one = Directory.CreateDirectory(Path.Combine(first, "account", "workspace")).FullName;
            var two = Directory.CreateDirectory(Path.Combine(second, "account", "workspace")).FullName;
            File.WriteAllText(Path.Combine(one, "local_broken.json"), "{invalid");
            CopyFixture("archived.json", one, "local_archived.json");
            CopyFixture("invalid-id.json", one, "local_invalid.json");
            Assert.Null(ClaudeDesktopSessionReader.FindSession([first], CliSession));
            CopyFixture("session.json", two, "local_sample.json");
            Assert.Equal("local_00000000-0000-4000-8000-000000000002",
                ClaudeDesktopSessionReader.FindSession([Path.Combine(root, "missing"), first, second], CliSession));
            Assert.Null(ClaudeDesktopSessionReader.FindSession([second], "00000000-0000-4000-8000-000000000099"));
            Assert.Null(ClaudeDesktopSessionReader.FindSession([second], "../invalid"));
            CopyFixture("newer.json", one, "local_newer.json");
            Assert.Equal("local_00000000-0000-4000-8000-000000000004",
                ClaudeDesktopSessionReader.FindSession([second, first], CliSession));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    static void CopyFixture(string fixture, string directory, string filename) =>
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-desktop", fixture), Path.Combine(directory, filename));
}
