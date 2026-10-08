using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;

namespace AiUsage.Tests;

/// <summary>
/// Copilot reads through the GitHub CLI's own sign-in. These tests drive the provider with a canned
/// <see cref="CopilotFetch"/> instead of running gh, so each outcome's snapshot is proven without the
/// tool present. The response body is the real copilot_internal/user shape with invented numbers.
/// </summary>
public class CopilotProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-13T12:00:00Z");

    private const string UserJson = """
        {
          "login": "octocat",
          "quota_reset_date_utc": "2026-10-01T00:00:00.000Z",
          "quota_snapshots": {
            "chat": { "percent_remaining": 80.0, "unlimited": false, "has_quota": true },
            "completions": { "percent_remaining": 100.0, "unlimited": false, "has_quota": true },
            "premium_interactions": { "percent_remaining": 0.0, "unlimited": false, "has_quota": false }
          }
        }
        """;

    private static CopilotProvider Provider(CopilotFetch fetch) =>
        new((_) => Task.FromResult(fetch), () => Now);

    [Fact]
    public async Task A_signed_in_read_fills_one_window_per_real_quota_bucket()
    {
        var snapshot = await Provider(new CopilotFetch(GitHubCliOutcome.Ok, UserJson)).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.LocalLogin, snapshot.SourceKind);

        // premium_interactions has has_quota=false, so it is skipped; chat and completions remain.
        Assert.Equal(2, snapshot.Windows.Count);
        var chat = snapshot.Windows.Single(w => w.Label == "Window_CopilotChat");
        Assert.Equal(20, chat.UsedPercent, precision: 3);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00Z"), chat.ResetsAt);
    }

    [Fact]
    public async Task A_signed_in_read_names_the_github_login_as_the_account_label()
    {
        var snapshot = await Provider(new CopilotFetch(GitHubCliOutcome.Ok, UserJson)).FetchAsync(CancellationToken.None);

        Assert.Equal("octocat", snapshot.AccountLabel);
    }

    [Fact]
    public async Task Not_signed_in_shows_the_sign_in_state()
    {
        var snapshot = await Provider(CopilotFetch.NotSignedIn).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotSignedIn, snapshot.Status);
        Assert.Empty(snapshot.Windows);
    }

    // The CLI is present but nobody is signed into it - the app must never run "gh auth login"
    // itself, so the tile's own notice names the command for the user to run.
    [Fact]
    public async Task Not_signed_in_names_the_gh_auth_login_command_instead_of_a_generic_notice()
    {
        var snapshot = await Provider(CopilotFetch.NotSignedIn).FetchAsync(CancellationToken.None);

        Assert.Equal("Status_GitHubCliNotSignedIn_Reason", snapshot.Error?.ReasonKey);
    }

    [Fact]
    public async Task A_missing_cli_reports_source_unavailable_with_a_reason_but_no_sign_in_action()
    {
        var snapshot = await Provider(CopilotFetch.ToolMissing).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SourceUnavailable, snapshot.Status);
        Assert.Equal("Status_GitHubCliMissing_Reason", snapshot.Error?.ReasonKey);
        Assert.Null(snapshot.Error?.ActionKey);
    }

    [Fact]
    public async Task A_failed_read_reports_a_failure_not_a_zero()
    {
        var snapshot = await Provider(CopilotFetch.Failed).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Failed, snapshot.Status);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public void A_bucket_with_a_percent_remaining_outside_0_to_100_is_dropped_while_a_valid_bucket_still_shows()
    {
        const string json = """
            {
              "quota_reset_date_utc": "2026-10-01T00:00:00.000Z",
              "quota_snapshots": {
                "chat": { "percent_remaining": -10.0, "unlimited": false, "has_quota": true },
                "completions": { "percent_remaining": 80.0, "unlimited": false, "has_quota": true }
              }
            }
            """;

        var windows = AiUsage.Providers.Parsing.CopilotUserParser.Parse(json);

        var window = Assert.Single(windows);
        Assert.Equal("Window_CopilotCompletions", window.Label);
        Assert.Equal(20, window.UsedPercent, precision: 3);
    }

    [Fact]
    public void Copilot_signs_in_through_the_github_cli_outside_the_app()
    {
        var provider = Provider(CopilotFetch.NotSignedIn);

        Assert.False(provider.SupportsInAppSignIn);
    }

    [Fact]
    public async Task FetchAsync_reports_offline_once_the_single_candidate_is_routed_through_the_shared_chooser()
    {
        AiUsage.Services.NetworkStatus.Probe = () => false;
        try
        {
            var snapshot = await Provider(CopilotFetch.Failed).FetchAsync(CancellationToken.None);

            Assert.Equal(ProviderStatus.Failed, snapshot.Status);
            Assert.Equal("Status_Offline_Reason", snapshot.Error?.ReasonKey);
        }
        finally
        {
            AiUsage.Services.NetworkStatus.Probe = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable;
        }
    }
}
