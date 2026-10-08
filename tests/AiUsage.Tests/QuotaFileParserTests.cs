using AiUsage.Providers.Parsing;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// CopilotUsageParser reads a speculative "quotas" array and, before this file existed, only had
/// indirect provider-level coverage - which is exactly how a malformed field type (a string instead
/// of a number) escaping straight to an unguarded JsonElement.TryGetDouble went unnoticed.
/// </summary>
public class QuotaFileParserTests
{
    [Fact]
    public void Copilot_parses_a_well_formed_quota_entry()
    {
        var json = """{"quotas":[{"used_percent":30.0,"resets_at":"2026-10-01T00:00:00Z"}]}""";

        var windows = CopilotUsageParser.TryParse(json);

        Assert.Single(windows);
        Assert.Equal(30.0, windows[0].UsedPercent);
    }

    [Fact]
    public void Copilot_skips_an_entry_whose_percent_is_a_string_instead_of_throwing()
    {
        var json = """{"quotas":[{"used_percent":"30","resets_at":"2026-10-01T00:00:00Z"}]}""";

        var windows = CopilotUsageParser.TryParse(json);

        Assert.Empty(windows);
    }
}
