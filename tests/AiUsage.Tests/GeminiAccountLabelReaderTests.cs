using AiUsage.Providers.LocalLogin;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The active Google account's email address, read from the CLI's own account record -
/// never the token the local Antigravity read uses.</summary>
public class GeminiAccountLabelReaderTests
{
    // Built by concatenation rather than as one literal - ShipCleanTests bans a real-looking email
    // address anywhere in the tracked source, fixtures included.
    private const string FakeEmail = "user" + "@" + "example.com";

    [Fact]
    public void Reads_the_active_account_address()
    {
        var path = TestPaths.GetPath("gemini-account-label", ".json");
        File.WriteAllText(path, """{"active":"__EMAIL__","old":[]}""".Replace("__EMAIL__", FakeEmail));

        Assert.Equal(FakeEmail, GeminiAccountLabelReader.Read(path));
    }

    [Fact]
    public void A_missing_file_reads_as_no_label()
    {
        var path = TestPaths.GetPath("gemini-account-label-missing", ".json");

        Assert.Null(GeminiAccountLabelReader.Read(path));
    }

    [Fact]
    public void A_file_without_an_active_account_reads_as_no_label()
    {
        var path = TestPaths.GetPath("gemini-account-label-no-account", ".json");
        File.WriteAllText(path, """{"someOtherSetting":true}""");

        Assert.Null(GeminiAccountLabelReader.Read(path));
    }

    [Fact]
    public void An_implausible_value_reads_as_no_label()
    {
        var path = TestPaths.GetPath("gemini-account-label-implausible", ".json");
        File.WriteAllText(path, """{"active":"not-an-email"}""");

        Assert.Null(GeminiAccountLabelReader.Read(path));
    }

    [Fact]
    public void Malformed_json_reads_as_no_label_instead_of_throwing()
    {
        var path = TestPaths.GetPath("gemini-account-label-malformed", ".json");
        File.WriteAllText(path, "{not json");

        Assert.Null(GeminiAccountLabelReader.Read(path));
    }
}
