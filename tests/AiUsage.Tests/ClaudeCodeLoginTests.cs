using System.Text;
using AiUsage.Providers.LocalLogin;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Three of ClaudeCodeLogin.FetchAsync's four exits without usable windows, each
/// proven against a real (fixture) credentials file, each carrying its own plain reason word instead
/// of the shared NotSignedIn/Failed pair it used to. The fourth exit (a non-200 answer from the usage
/// endpoint) needs a live HTTP call and is covered instead at ClaudeProviderTests through the
/// provider's own injection seam, the same way GitHubCliUsage and AntigravityLocalLogin are tested.</summary>
public class ClaudeCodeLoginTests
{
    private const string FakeToken = "sk-ant-oat01-FAKE-TOKEN-MARKER-DO-NOT-LEAK";

    [Fact]
    public async Task A_missing_credentials_file_reports_the_missing_file_reason()
    {
        var path = TestPaths.GetPath("claude-code-login-missing", ".json");

        var usage = await ClaudeCodeLogin.FetchAsync(path, CancellationToken.None);

        Assert.Equal(LocalLoginOutcome.NotSignedIn, usage.Outcome);
        Assert.Equal(ClaudeCodeLoginReason.MissingFile, usage.Reason);
    }

    [Fact]
    public async Task A_credentials_file_without_an_access_token_reports_the_no_token_reason()
    {
        using var directory = TestPaths.CreateDisposableDirectory("claude-code-login-no-token");
        var path = Path.Combine(directory, "credentials.json");
        File.WriteAllText(path, """{"claudeAiOauth":{"scopes":["user:inference"]}}""", new UTF8Encoding(false));

        var usage = await ClaudeCodeLogin.FetchAsync(path, CancellationToken.None);

        Assert.Equal(LocalLoginOutcome.NotSignedIn, usage.Outcome);
        Assert.Equal(ClaudeCodeLoginReason.NoToken, usage.Reason);
        Assert.DoesNotContain(FakeToken, usage.ToString());
    }

    [Fact]
    public async Task An_expired_token_reports_the_token_expired_reason_without_calling_the_endpoint()
    {
        using var directory = TestPaths.CreateDisposableDirectory("claude-code-login-expired");
        var path = Path.Combine(directory, "credentials.json");
        var expiredAtMs = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        var json = "{\"claudeAiOauth\":{\"accessToken\":\"" + FakeToken + "\",\"expiresAt\":" + expiredAtMs + "}}";
        File.WriteAllText(path, json, new UTF8Encoding(false));

        // An already-expired token is caught before the HTTP call, so this never reaches the network -
        // a live 401 would prove the same reason, but only this path proves it without one.
        var usage = await ClaudeCodeLogin.FetchAsync(path, CancellationToken.None);

        Assert.Equal(LocalLoginOutcome.NotSignedIn, usage.Outcome);
        Assert.Equal(ClaudeCodeLoginReason.TokenExpired, usage.Reason);
        Assert.DoesNotContain(FakeToken, usage.ToString());
    }
}
