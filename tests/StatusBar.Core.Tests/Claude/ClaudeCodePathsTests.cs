using StatusBar.Core.Claude;

namespace StatusBar.Core.Tests.Claude;

public class ClaudeCodePathsTests
{
    [Fact]
    public void Settings_override_takes_precedence_over_environment_and_profile()
    {
        var paths = ClaudeCodePaths.Resolve("settings-home", "environment-home", "profile-home");

        Assert.Equal("settings-home", paths.Home);
        Assert.Equal(Path.Combine("settings-home", "projects"), paths.ProjectsDirectory);
    }

    [Fact]
    public void Environment_override_precedes_the_default_profile_home()
    {
        var paths = ClaudeCodePaths.Resolve(environmentConfigDir: "environment-home", userProfile: "profile-home");

        Assert.Equal("environment-home", paths.Home);
        Assert.Equal(Path.Combine("environment-home", "projects"), paths.ProjectsDirectory);
    }

    [Fact]
    public void Default_home_is_the_claude_folder_under_the_user_profile()
    {
        var paths = ClaudeCodePaths.Resolve(userProfile: "profile-home");

        Assert.Equal(Path.Combine("profile-home", ".claude"), paths.Home);
        Assert.Equal(Path.Combine("profile-home", ".claude", "projects"), paths.ProjectsDirectory);
    }
}
