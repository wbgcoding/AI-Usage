using System.Text.Json;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;

namespace AiUsage.Tests;

/// <summary>One rule for what an account address has to look like, shared by the two local readers
/// and the web session read.</summary>
public class AccountLabelsTests
{
    // Built by concatenation rather than as one literal - ShipCleanTests bans a real-looking email
    // address anywhere in the tracked source.
    private const string FakeEmail = "user" + "@" + "example.com";

    // A right-to-left override in the middle of an otherwise ordinary address.
    private static readonly string BidiEmail = "user‮" + "@" + "example.com";

    [Fact]
    public void An_ordinary_address_passes()
    {
        Assert.True(AccountLabels.LooksLikeEmail(FakeEmail));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign.example.com")]
    [InlineData("two@@example")]
    [InlineData("a@b@example")]
    [InlineData("user\u0007@example")]
    [InlineData("user\u2066@example")]
    [InlineData("user\u200F@example")]
    [InlineData("user\n@example")]
    public void A_text_that_is_not_a_plain_single_address_is_refused(string? value)
    {
        Assert.False(AccountLabels.LooksLikeEmail(value));
    }

    [Fact]
    public void An_address_over_the_length_limit_is_refused()
    {
        Assert.False(AccountLabels.LooksLikeEmail(new string('a', 250) + "@" + "example.com"));
        Assert.True(AccountLabels.LooksLikeEmail(new string('a', 240) + "@" + "example.com"));
    }

    [Fact]
    public void The_Claude_Code_record_reader_refuses_a_bidi_address()
    {
        var path = TestPaths.GetPath("account-labels-claude", ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { oauthAccount = new { emailAddress = BidiEmail } }));

        Assert.Null(ClaudeAccountLabelReader.Read(path));
    }

    [Fact]
    public void The_Gemini_account_file_reader_refuses_a_bidi_address()
    {
        var path = TestPaths.GetPath("account-labels-gemini", ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { active = BidiEmail }));

        Assert.Null(GeminiAccountLabelReader.Read(path));
    }

    [Fact]
    public void The_web_session_reader_refuses_a_bidi_address_and_keeps_an_ordinary_one()
    {
        using var bad = JsonDocument.Parse(JsonSerializer.Serialize(new { email = BidiEmail }));
        using var good = JsonDocument.Parse(JsonSerializer.Serialize(new { email = FakeEmail }));

        Assert.Null(WebUsageSource.ReadAccountLabel(bad.RootElement));
        Assert.Equal(FakeEmail, WebUsageSource.ReadAccountLabel(good.RootElement));
    }
}
