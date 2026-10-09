using AiUsage.Providers.LocalLogin;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Pure parsing of <c>gh auth status</c>'s own text - the one part of
/// <see cref="GitHubCliUsage.ListSignedInUsersAsync"/> testable without a real <c>gh</c> process,
/// the same seam <see cref="CopilotUserParserTests"/> already uses for the sibling JSON endpoint.</summary>
public class GitHubCliUsageTests
{
    // A representative multi-account, multi-host gh auth status report - two hosts, two accounts on
    // one of them, one of them active.
    private const string TwoAccountsOneActive = """
        github.com
          ✓ Logged in to github.com account octocat (keyring)
          - Active account: true
          - Git operations protocol: https
          - Token: gho_************************************
        github.com
          ✓ Logged in to github.com account monalisa (keyring)
          - Active account: false
          - Git operations protocol: https
          - Token: gho_************************************
        """;

    [Fact]
    public void ParseAuthStatus_reads_every_account_and_which_one_is_active()
    {
        var accounts = GitHubCliUsage.ParseAuthStatus(TwoAccountsOneActive);

        Assert.Equal(2, accounts.Count);
        Assert.Contains(accounts, a => a is { Host: "github.com", Login: "octocat", Active: true });
        Assert.Contains(accounts, a => a is { Host: "github.com", Login: "monalisa", Active: false });
    }

    [Fact]
    public void ParseAuthStatus_of_a_single_account_report_reads_one_active_account()
    {
        const string text = """
            github.com
              ✓ Logged in to github.com account octocat (keyring)
              - Active account: true
              - Git operations protocol: https
            """;

        var accounts = GitHubCliUsage.ParseAuthStatus(text);

        Assert.Single(accounts);
        Assert.Equal(new GitHubAccount("github.com", "octocat", true), accounts[0]);
    }

    [Fact]
    public void ParseAuthStatus_of_a_not_signed_in_report_reads_no_accounts()
    {
        const string text = "You are not logged into any GitHub hosts.";

        Assert.Empty(GitHubCliUsage.ParseAuthStatus(text));
    }

    [Fact]
    public void ParseAuthStatus_of_unrelated_text_reads_no_accounts_instead_of_throwing()
    {
        Assert.Empty(GitHubCliUsage.ParseAuthStatus("not the gh auth status format at all"));
    }

    [Fact]
    public void ParseAuthStatus_of_empty_text_reads_no_accounts()
    {
        Assert.Empty(GitHubCliUsage.ParseAuthStatus(""));
    }

    // Same PATH lookup FetchAsync itself already makes before ever running the tool - a bool a
    // caller can check up front (the sign-in button's install offer) rather than always trying and
    // failing a read first. Whichever way it answers on the machine running this test, it must never
    // throw for a missing tool.
    [Fact]
    public void IsCliInstalled_answers_without_throwing()
    {
        var installed = GitHubCliUsage.IsCliInstalled();

        Assert.IsType<bool>(installed);
    }

    [Fact]
    public void ATokenTravelsInTheEnvironmentAndNeverInTheArguments()
    {
        const string token = "ghp_secretvalue123";

        var environment = GitHubCliUsage.TokenEnvironment("  " + token + " ");

        Assert.Equal(token, environment["GH_TOKEN"]);
        Assert.DoesNotContain(GitHubCliUsage.ApiArguments, a => a.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunGhAsync_hands_the_environment_to_the_child_process()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string[] arguments = ["/c", "echo %GH_TOKEN%"];

        var (outcome, output) = await GitHubCliUsage.RunGhAsync(
            cmd, arguments, CancellationToken.None, environment: GitHubCliUsage.TokenEnvironment("tok-from-env"));

        Assert.Equal(GitHubCliOutcome.Ok, outcome);
        Assert.Equal("tok-from-env", output);
        Assert.DoesNotContain(arguments, a => a.Contains("tok-from-env", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("To get started with GitHub CLI, please run:  gh auth login", true)]
    [InlineData("gh: Bad credentials (HTTP 401)", true)]
    [InlineData("error: could not read the authentication settings", false)]
    [InlineData("gh: API rate limit exceeded (HTTP 403)", false)]
    [InlineData("dial tcp: lookup api.github.com: no such host", false)]
    public void A_failed_run_is_signed_out_only_on_the_login_hint_or_a_401(string output, bool signedOut)
    {
        Assert.Equal(signedOut ? GitHubCliOutcome.NotSignedIn : GitHubCliOutcome.Failed, GitHubCliUsage.ClassifyFailure(output));
    }

    [Fact]
    public void The_token_request_names_the_github_com_host()
    {
        var arguments = GitHubCliUsage.TokenArguments("octocat");

        Assert.Equal(["auth", "token", "--hostname", "github.com", "--user", "octocat"], arguments);
    }

    [Fact]
    public void Only_github_com_accounts_are_offered()
    {
        GitHubAccount[] accounts =
        [
            new("github.com", "octocat", Active: true),
            new("ghe.example.org", "corp-user", Active: true),
            new("GitHub.com", "second", Active: false),
        ];

        var offered = GitHubCliUsage.OnlyGitHubDotCom(accounts);

        Assert.Equal(["octocat", "second"], offered.Select(account => account.Login));
    }
}
