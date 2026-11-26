using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;

namespace AiUsage.Tests;

[Trait("Layer", "Logic")]
public class InstalledAgentsTests
{
    private static readonly Dictionary<string, IReadOnlyList<string>> Candidates = new()
    {
        ["claude"] = [@"C:\home\.claude", @"C:\home\.config\claude"],
        ["codex"] = [@"C:\home\.codex"],
        ["cursor"] = [@"C:\roaming\Cursor"],
        ["gemini"] = [@"C:\home\.gemini", @"C:\local\agy\bin\agy.exe"],
        ["copilot"] = [@"C:\roaming\GitHub CLI"],
    };

    [Fact]
    public void Only_agents_with_an_existing_folder_or_program_count_as_installed()
    {
        var existing = new HashSet<string> { @"C:\home\.config\claude", @"C:\local\agy\bin\agy.exe" };

        var found = InstalledAgents.Detect(Candidates, existing.Contains, () => false);

        Assert.Equal(["claude", "gemini"], found.Order());
    }

    [Fact]
    public void Copilot_counts_when_the_GitHub_CLI_is_on_the_path()
    {
        var found = InstalledAgents.Detect(Candidates, _ => false, () => true);

        Assert.Equal(["copilot"], found);
    }

    [Fact]
    public void The_first_start_shows_exactly_the_detected_agents()
    {
        var settings = new AppSettings();

        InstalledAgents.ApplyFirstStartVisibility(settings, new HashSet<string> { "codex", "cursor" });

        Assert.Equal(["codex", "cursor"], AppSettings.KnownProviderIds.Where(id => settings.Providers[id].Visible).Order());
    }

    [Fact]
    public void Nothing_detected_shows_only_Claude()
    {
        var settings = new AppSettings();

        InstalledAgents.ApplyFirstStartVisibility(settings, new HashSet<string>());

        Assert.Equal(["claude"], AppSettings.KnownProviderIds.Where(id => settings.Providers[id].Visible));
    }

    [Fact]
    public void The_day_grid_tile_is_left_as_it_is()
    {
        var settings = new AppSettings();
        var before = settings.Providers.TryGetValue(AppSettings.DayGridTileId, out var grid) ? grid.Visible : (bool?)null;

        InstalledAgents.ApplyFirstStartVisibility(settings, new HashSet<string> { "codex" });

        Assert.Equal(before, settings.Providers.TryGetValue(AppSettings.DayGridTileId, out var after) ? after.Visible : null);
    }

    [Fact]
    public void Only_a_profile_without_settings_and_backup_counts_as_a_first_start()
    {
        using var directory = TestPaths.CreateDisposableDirectory("installed-agents-fresh");
        using (var store = new SettingsStore(directory))
        {
            var settings = store.Load();
            Assert.True(store.LastLoadWasFresh);
            store.SaveNow(settings);
            store.SaveNow(settings);
        }

        using (var store = new SettingsStore(directory))
        {
            store.Load();
            Assert.False(store.LastLoadWasFresh);
        }

        // Only the backup left: an existing user whose file went missing, not a first start.
        File.Delete(Path.Combine(directory, "settings.json"));
        using (var store = new SettingsStore(directory))
        {
            store.Load();
            Assert.False(store.LastLoadWasFresh);
        }
    }
}
