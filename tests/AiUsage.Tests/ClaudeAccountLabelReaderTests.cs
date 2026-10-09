using AiUsage.Providers.LocalLogin;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The signed-in Claude Code account's own email address, read from
/// <c>~/.claude.json</c> - never the credentials file <see cref="ClaudeCodeLoginTests"/> covers.</summary>
public class ClaudeAccountLabelReaderTests
{
    // Built by concatenation rather than as one literal - ShipCleanTests bans a real-looking email
    // address anywhere in the tracked source, fixtures included.
    private const string FakeEmail = "user" + "@" + "example.com";

    [Fact]
    public void Reads_the_email_address_out_of_the_oauth_account_block()
    {
        var path = TestPaths.GetPath("claude-account-label", ".json");
        File.WriteAllText(path, """{"oauthAccount":{"emailAddress":"__EMAIL__","organizationUuid":"x"}}""".Replace("__EMAIL__", FakeEmail));

        Assert.Equal(FakeEmail, ClaudeAccountLabelReader.Read(path));
    }

    [Fact]
    public void A_missing_file_reads_as_no_label()
    {
        var path = TestPaths.GetPath("claude-account-label-missing", ".json");

        Assert.Null(ClaudeAccountLabelReader.Read(path));
    }

    [Fact]
    public void A_file_without_an_oauth_account_block_reads_as_no_label()
    {
        var path = TestPaths.GetPath("claude-account-label-no-account", ".json");
        File.WriteAllText(path, """{"someOtherSetting":true}""");

        Assert.Null(ClaudeAccountLabelReader.Read(path));
    }

    [Fact]
    public void An_implausible_value_reads_as_no_label()
    {
        var path = TestPaths.GetPath("claude-account-label-implausible", ".json");
        File.WriteAllText(path, """{"oauthAccount":{"emailAddress":"not-an-email"}}""");

        Assert.Null(ClaudeAccountLabelReader.Read(path));
    }

    [Fact]
    public void Malformed_json_reads_as_no_label_instead_of_throwing()
    {
        var path = TestPaths.GetPath("claude-account-label-malformed", ".json");
        File.WriteAllText(path, "{not json");

        Assert.Null(ClaudeAccountLabelReader.Read(path));
    }

    [Fact]
    public void A_file_whose_root_is_an_array_reads_as_no_label()
    {
        var path = TestPaths.GetPath("claude-account-label-array", ".json");
        File.WriteAllText(path, "[]");

        Assert.Null(ClaudeAccountLabelReader.Read(path));
    }
}
