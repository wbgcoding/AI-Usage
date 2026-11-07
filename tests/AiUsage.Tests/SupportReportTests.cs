using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Same guard <see cref="PrivacyTests"/> already sets up for a single tile's own
/// <c>DiagnosticsText</c>, asserted from the other end: the combined report every tile feeds into
/// must never carry an account label either, and the one path it does include (the data folder)
/// must never carry the real user name.</summary>
public class SupportReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_never_includes_the_account_label_and_replaces_the_user_name_in_the_data_folder_path()
    {
        const string label = "Beispiel GmbH";
        var snapshot = new ProviderSnapshot(
            ProviderId: "claude",
            Windows: [],
            PlanType: "Plus",
            SourceKind: SourceKind.WebSession,
            FetchedAt: Now,
            DataTimestamp: Now,
            Status: ProviderStatus.Ok,
            Error: null,
            Diagnostics: ["Gefunden über die Organisations-API."],
            AccountLabel: label);
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(snapshot, Now);

        var report = SupportReport.Build([tile]);

        Assert.DoesNotContain(label, report);
        // AppPaths.DataDirectory resolves under the real profile folder in this test process too -
        // proving the real user name never survives is a stronger check than any fixture path could be.
        Assert.DoesNotContain(Environment.UserName, report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_masks_a_user_path_and_a_token_in_a_diagnostics_line_and_the_tile_text()
    {
        var token = "ghp_" + new string('a', 36);
        var userPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "project", "log.txt");
        var snapshot = new ProviderSnapshot(
            ProviderId: "claude",
            Windows: [],
            PlanType: "Plus",
            SourceKind: SourceKind.WebSession,
            FetchedAt: Now,
            DataTimestamp: Now,
            Status: ProviderStatus.Ok,
            Error: null,
            Diagnostics: [$"Read failed at {userPath} with {token}"]);
        var tile = new ProviderTileViewModel("claude", $"Claude {token}");
        tile.Apply(snapshot, Now);

        var report = SupportReport.Build([tile]);

        Assert.Contains("Read failed at", report);
        Assert.DoesNotContain(token, report);
        Assert.DoesNotContain(Environment.UserName, report, StringComparison.OrdinalIgnoreCase);
    }
}
