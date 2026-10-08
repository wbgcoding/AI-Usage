using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;
using AiUsage.Stats;

namespace AiUsage.Tests;

public class ClaudeConfigRootTests
{
    [Fact]
    public void A_variable_naming_a_missing_folder_falls_back_to_the_default_roots()
    {
        using var profile = TestPaths.CreateDisposableDirectory("ccr-profile");
        var missing = Path.Combine(profile, "does-not-exist");

        var candidates = ClaudeConfigRoot.Candidates(missing, profile);

        Assert.Equal([Path.Combine(profile, ".claude"), Path.Combine(profile, ".config", "claude")], candidates);
    }

    [Fact]
    public void A_variable_naming_an_existing_folder_replaces_the_defaults_for_stats_and_provider_paths()
    {
        using var profile = TestPaths.CreateDisposableDirectory("ccr-profile2");
        using var custom = TestPaths.CreateDisposableDirectory("ccr-custom");
        Directory.CreateDirectory(Path.Combine(custom, "projects"));
        Directory.CreateDirectory(Path.Combine(profile, ".claude", "projects"));
        var missing = Path.Combine(profile, "nope");
        var value = $"{missing}, {custom.Path}";

        Assert.Equal([Path.Combine(custom, "projects")], StatsIndexer.ClaudeProjectsRootsFor(value, profile));
        Assert.Equal(Path.Combine(custom, "projects"), ClaudeProvider.ProjectsRootFor(value, profile));
        Assert.Equal(Path.Combine(custom, ".credentials.json"), ClaudeCodeLogin.CredentialsPathFor(value, profile));
    }

    [Fact]
    public void Without_the_variable_the_classic_layout_is_used()
    {
        using var profile = TestPaths.CreateDisposableDirectory("ccr-profile3");
        Directory.CreateDirectory(Path.Combine(profile, ".claude", "projects"));

        Assert.Equal([Path.Combine(profile, ".claude", "projects")], StatsIndexer.ClaudeProjectsRootsFor(null, profile));
        Assert.Equal(Path.Combine(profile, ".claude", "projects"), ClaudeProvider.ProjectsRootFor(null, profile));
        Assert.Equal(Path.Combine(profile, ".claude", ".credentials.json"), ClaudeCodeLogin.CredentialsPathFor(null, profile));
    }

    [Fact]
    public void The_account_file_is_read_from_the_custom_folder_when_it_holds_one()
    {
        using var profile = TestPaths.CreateDisposableDirectory("ccr-profile4");
        using var custom = TestPaths.CreateDisposableDirectory("ccr-custom4");
        File.WriteAllText(Path.Combine(custom, ".claude.json"), "{}");

        Assert.Equal(Path.Combine(custom, ".claude.json"), ClaudeAccountLabelReader.PathFor(custom, profile));
        Assert.Equal(Path.Combine(profile, ".claude.json"), ClaudeAccountLabelReader.PathFor(null, profile));
    }

    [Fact]
    public void Without_the_variable_a_stale_file_inside_the_default_folder_is_ignored()
    {
        using var profile = TestPaths.CreateDisposableDirectory("ccr-profile5");
        Directory.CreateDirectory(Path.Combine(profile, ".claude"));
        File.WriteAllText(Path.Combine(profile, ".claude", ".claude.json"), "{}");
        File.WriteAllText(Path.Combine(profile, ".claude.json"), "{}");

        Assert.Equal(Path.Combine(profile, ".claude.json"), ClaudeAccountLabelReader.PathFor(null, profile));
        Assert.Equal(Path.Combine(profile, ".claude.json"), ClaudeAccountLabelReader.PathFor(Path.Combine(profile, "missing"), profile));
    }
}
