using AiUsage.Models;
using AiUsage.Storage;

namespace AiUsage.Tests;

/// <summary>Renaming of the plain English window labels older files hold: single labels, the tray
/// window choice and a whole loaded settings object.</summary>
public class LegacyWindowLabelsTests
{
    private const string Prefix = "Label:";

    [Theory]
    [InlineData("copilot", "Chat", "Window_CopilotChat")]
    [InlineData("copilot", "Completions", "Window_CopilotCompletions")]
    [InlineData("copilot", "Premium", "Window_CopilotPremium")]
    [InlineData("copilot#2", "Premium", "Window_CopilotPremium")]
    public void A_legacy_copilot_label_is_renamed(string account, string label, string expected) =>
        Assert.Equal(expected, LegacyWindowLabels.Migrate(account, label));

    [Theory]
    [InlineData("codex", "Chat")]
    [InlineData("claude#2", "Premium")]
    [InlineData("copilot", "chat")]
    [InlineData("copilot", "Window_CopilotChat")]
    [InlineData("copilot", "")]
    [InlineData("copilot", "Weekly")]
    [InlineData("", "Chat")]
    public void Any_other_account_or_label_is_returned_unchanged(string account, string label) =>
        Assert.Equal(label, LegacyWindowLabels.Migrate(account, label));

    [Theory]
    [InlineData("Label:Chat", "Label:Window_CopilotChat")]
    [InlineData("Label:Completions", "Label:Window_CopilotCompletions")]
    [InlineData("Label:Premium", "Label:Window_CopilotPremium")]
    public void A_tray_window_with_a_legacy_label_is_renamed_behind_its_prefix(string stored, string expected) =>
        Assert.Equal(expected, LegacyWindowLabels.MigrateTrayWindow(stored, Prefix));

    [Theory]
    [InlineData("Auto")]
    [InlineData("Weekly")]
    [InlineData("")]
    [InlineData("Label:")]
    [InlineData("Label:Window_CopilotChat")]
    [InlineData("Label:Other")]
    [InlineData("label:Chat")]
    [InlineData("Chat")]
    public void Any_other_tray_window_is_returned_unchanged(string stored) =>
        Assert.Equal(stored, LegacyWindowLabels.MigrateTrayWindow(stored, Prefix));

    private static AppSettings SettingsWith(string trayProvider, string trayWindow, params (string Account, string[] Hidden)[] accounts)
    {
        var settings = new AppSettings { TrayProvider = trayProvider, TrayWindow = trayWindow, Providers = [] };
        foreach (var (account, hidden) in accounts)
            settings.Providers[account] = new ProviderSettings { HiddenWindows = [.. hidden] };
        return settings;
    }

    [Fact]
    public void Hidden_windows_of_a_copilot_account_are_renamed()
    {
        var settings = SettingsWith("Auto", "Auto", ("copilot", ["Chat", "Premium", "Window_Weekly"]));

        LegacyWindowLabels.Migrate(settings, Prefix);

        Assert.Equal(["Window_CopilotChat", "Window_CopilotPremium", "Window_Weekly"], settings.Providers["copilot"].HiddenWindows);
    }

    [Fact]
    public void Hidden_windows_of_other_providers_keep_their_labels()
    {
        var settings = SettingsWith("Auto", "Auto", ("codex", ["Chat"]), ("copilot#2", ["Completions"]));

        LegacyWindowLabels.Migrate(settings, Prefix);

        Assert.Equal(["Chat"], settings.Providers["codex"].HiddenWindows);
        Assert.Equal(["Window_CopilotCompletions"], settings.Providers["copilot#2"].HiddenWindows);
    }

    [Fact]
    public void A_rename_that_makes_a_duplicate_keeps_one_entry()
    {
        var settings = SettingsWith("Auto", "Auto", ("copilot", ["Chat", "Window_CopilotChat"]));

        LegacyWindowLabels.Migrate(settings, Prefix);

        Assert.Equal(["Window_CopilotChat"], settings.Providers["copilot"].HiddenWindows);
    }

    [Fact]
    public void A_null_entry_in_the_hidden_list_is_dropped_instead_of_throwing()
    {
        var settings = SettingsWith("Auto", "Auto", ("copilot", ["Chat", null!]));

        LegacyWindowLabels.Migrate(settings, Prefix);

        Assert.Equal(["Window_CopilotChat"], settings.Providers["copilot"].HiddenWindows);
    }

    [Fact]
    public void An_empty_hidden_list_stays_empty()
    {
        var settings = SettingsWith("Auto", "Auto", ("copilot", []));

        LegacyWindowLabels.Migrate(settings, Prefix);

        Assert.Empty(settings.Providers["copilot"].HiddenWindows);
    }

    [Fact]
    public void No_accounts_at_all_is_fine()
    {
        var settings = SettingsWith("Auto", "Auto");

        LegacyWindowLabels.Migrate(settings, Prefix);

        Assert.Empty(settings.Providers);
    }

    [Theory]
    [InlineData("Auto")]
    [InlineData("copilot")]
    [InlineData("copilot#3")]
    public void The_tray_window_is_renamed_when_the_icon_follows_copilot(string trayProvider)
    {
        var settings = SettingsWith(trayProvider, "Label:Premium");

        LegacyWindowLabels.Migrate(settings, Prefix);

        Assert.Equal("Label:Window_CopilotPremium", settings.TrayWindow);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude#2")]
    [InlineData("cursor")]
    public void The_same_word_is_left_alone_when_another_provider_is_pinned(string trayProvider)
    {
        var settings = SettingsWith(trayProvider, "Label:Premium");

        LegacyWindowLabels.Migrate(settings, Prefix);

        Assert.Equal("Label:Premium", settings.TrayWindow);
    }

    [Fact]
    public void A_non_label_tray_window_is_not_touched()
    {
        var settings = SettingsWith("Auto", "Weekly");

        LegacyWindowLabels.Migrate(settings, Prefix);

        Assert.Equal("Weekly", settings.TrayWindow);
    }

    [Fact]
    public void Migrating_twice_changes_nothing_more()
    {
        var settings = SettingsWith("copilot", "Label:Chat", ("copilot", ["Chat", "Completions"]));

        LegacyWindowLabels.Migrate(settings, Prefix);
        LegacyWindowLabels.Migrate(settings, Prefix);

        Assert.Equal(["Window_CopilotChat", "Window_CopilotCompletions"], settings.Providers["copilot"].HiddenWindows);
        Assert.Equal("Label:Window_CopilotChat", settings.TrayWindow);
    }
}
