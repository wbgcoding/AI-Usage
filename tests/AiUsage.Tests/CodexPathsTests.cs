using AiUsage.Providers;
using Xunit;

namespace AiUsage.Tests;

public class CodexPathsTests
{
    private const string Profile = @"D:\Profiles\someone";

    [Fact]
    public void CODEX_HOME_is_where_Codex_keeps_its_sessions_when_it_is_set()
    {
        Assert.Equal(@"D:\codex", CodexPaths.ResolveRoot(@"D:\codex", Profile, _ => true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_CODEX_HOME_the_folder_in_the_profile_is_read(string? codexHome)
    {
        Assert.Equal(Path.Combine(Profile, ".codex"), CodexPaths.ResolveRoot(codexHome, Profile, _ => true));
    }

    [Fact]
    public void A_CODEX_HOME_naming_a_missing_folder_falls_back_to_the_profile()
    {
        Assert.Equal(Path.Combine(Profile, ".codex"), CodexPaths.ResolveRoot(@"D:\gone", Profile, _ => false));
    }

    [Fact]
    public void The_shown_folder_follows_CODEX_HOME_without_naming_its_path()
    {
        Assert.Equal("%CODEX_HOME%/sessions", CodexPaths.DisplaySessions(@"D:\codex", _ => true));
        Assert.Equal("~/.codex/sessions", CodexPaths.DisplaySessions(@"D:\gone", _ => false));
        Assert.Equal("~/.codex/sessions", CodexPaths.DisplaySessions(null, _ => true));
    }
}
