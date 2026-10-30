using AiUsage.Providers;
using AiUsage.Providers.Parsing;
using Xunit;

namespace AiUsage.Tests;

public class ClaudeConsoleCreditParserTests
{
    private const string OrgId = "2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21cc";
    private const string SecondOrgId = "00000000-0000-4000-8000-000000000002";
    private const string ChatOnlyOrgId = "00000000-0000-4000-8000-000000000003";

    private static string Orgs(params (string Id, string[] Caps)[] orgs) =>
        "[" + string.Join(",", orgs.Select(o =>
            $"{{\"uuid\":\"{o.Id}\",\"name\":\"x\",\"capabilities\":[{string.Join(",", o.Caps.Select(c => $"\"{c}\""))}]}}")) + "]";

    [Fact]
    public void Parse_turns_cents_into_major_units()
    {
        var reading = ClaudeConsoleCreditParser.Parse("{\"amount\":1840,\"currency\":\"USD\"}");

        Assert.Equal(ClaudeConsoleCreditOutcome.Ok, reading.Outcome);
        Assert.Equal(18.40m, reading.Credit!.Amount);
        Assert.Equal("USD", reading.Credit.Currency);
    }

    [Fact]
    public void Parse_defaults_to_usd_without_a_currency()
    {
        var reading = ClaudeConsoleCreditParser.Parse("{\"amount\":5}");

        Assert.Equal(0.05m, reading.Credit!.Amount);
        Assert.Equal("USD", reading.Credit.Currency);
    }

    [Fact]
    public void Parse_uses_a_present_currency_in_upper_case()
    {
        var reading = ClaudeConsoleCreditParser.Parse("{\"amount\":250,\"currency\":\"eur\"}");

        Assert.Equal("EUR", reading.Credit!.Currency);
    }

    [Theory]
    [InlineData("{\"amount\":100,\"currency\":\"dollars\"}")]
    [InlineData("{\"amount\":100,\"currency\":7}")]
    [InlineData("{\"amount\":100,\"currency\":null}")]
    public void Parse_falls_back_to_usd_for_an_unusable_currency(string body)
    {
        Assert.Equal("USD", ClaudeConsoleCreditParser.Parse(body).Credit!.Currency);
    }

    [Fact]
    public void Parse_keeps_a_zero_balance_as_a_reading()
    {
        var reading = ClaudeConsoleCreditParser.Parse("{\"amount\":0}");

        Assert.Equal(ClaudeConsoleCreditOutcome.Ok, reading.Outcome);
        Assert.Equal(0m, reading.Credit!.Amount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>challenge</html>")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"balance\":100}")]
    [InlineData("{\"amount\":\"100\"}")]
    [InlineData("{\"amount\":null}")]
    public void Parse_is_unreadable_without_a_numeric_amount(string? body)
    {
        var reading = ClaudeConsoleCreditParser.Parse(body);

        Assert.Equal(ClaudeConsoleCreditOutcome.Unreadable, reading.Outcome);
        Assert.Null(reading.Credit);
    }

    [Fact]
    public void PickOrganization_prefers_the_last_active_api_organization()
    {
        var json = Orgs((OrgId, ["api"]), (SecondOrgId, ["chat", "api"]));

        var pick = ClaudeConsoleCreditParser.PickOrganization(json, SecondOrgId);

        Assert.Equal(ClaudeConsoleOrgOutcome.Found, pick.Outcome);
        Assert.Equal(SecondOrgId, pick.OrganizationId);
    }

    [Fact]
    public void PickOrganization_matches_the_cookie_value_in_any_letter_case()
    {
        var json = Orgs((OrgId, ["api"]), (SecondOrgId, ["api"]));

        Assert.Equal(SecondOrgId, ClaudeConsoleCreditParser.PickOrganization(json, SecondOrgId.ToUpperInvariant()).OrganizationId);
    }

    [Fact]
    public void PickOrganization_takes_the_first_api_organization_without_a_cookie()
    {
        var json = Orgs((ChatOnlyOrgId, ["chat"]), (OrgId, ["api"]), (SecondOrgId, ["api"]));

        var pick = ClaudeConsoleCreditParser.PickOrganization(json, null);

        Assert.Equal(OrgId, pick.OrganizationId);
    }

    [Fact]
    public void PickOrganization_ignores_a_last_active_organization_that_is_no_api_one()
    {
        var json = Orgs((ChatOnlyOrgId, ["chat"]), (OrgId, ["api"]));

        Assert.Equal(OrgId, ClaudeConsoleCreditParser.PickOrganization(json, ChatOnlyOrgId).OrganizationId);
    }

    [Fact]
    public void PickOrganization_reports_no_api_organization_for_chat_only_accounts()
    {
        var pick = ClaudeConsoleCreditParser.PickOrganization(Orgs((ChatOnlyOrgId, ["chat", "claude_pro"])), ChatOnlyOrgId);

        Assert.Equal(ClaudeConsoleOrgOutcome.NoApiOrganization, pick.Outcome);
        Assert.Null(pick.OrganizationId);
    }

    [Fact]
    public void PickOrganization_reports_no_api_organization_for_an_empty_list()
    {
        Assert.Equal(ClaudeConsoleOrgOutcome.NoApiOrganization, ClaudeConsoleCreditParser.PickOrganization("[]", null).Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"organizations\":[]}")]
    [InlineData("[{\"id\":\"x\",\"roles\":[\"api\"]}]")]
    public void PickOrganization_is_unreadable_for_a_changed_shape(string? json)
    {
        Assert.Equal(ClaudeConsoleOrgOutcome.Unreadable, ClaudeConsoleCreditParser.PickOrganization(json, null).Outcome);
    }

    [Fact]
    public void ChangeNotes_name_keys_but_never_values_or_ids()
    {
        var notes = ClaudeConsoleCreditParser.ChangeNotes(
            $"[{{\"id\":\"{OrgId}\",\"roles\":[\"api\"]}}]", "{\"balance\":1840,\"owner@example.com\":1}");

        Assert.Equal(2, notes.Count);
        var all = string.Join("\n", notes);
        Assert.Contains("id, roles", all);
        Assert.Contains("balance", all);
        Assert.DoesNotContain(OrgId, all);
        Assert.DoesNotContain("1840", all);
        Assert.DoesNotContain("owner@example.com", all);
    }

    [Fact]
    public void ChangeNotes_stay_empty_for_readable_text_and_for_non_json()
    {
        Assert.Empty(ClaudeConsoleCreditParser.ChangeNotes(Orgs((OrgId, ["api"])), "{\"amount\":100}"));
        Assert.Empty(ClaudeConsoleCreditParser.ChangeNotes("<html>", "<html>"));
        Assert.Empty(ClaudeConsoleCreditParser.ChangeNotes(null, null));
    }

    [Theory]
    [InlineData($"/api/organizations/{OrgId}/prepaid/credits", true)]
    [InlineData($"/api/organizations/{OrgId}/prepaid/credits\n", false)]
    [InlineData($"/api/organizations/{OrgId}/prepaid/credits?x=1", false)]
    [InlineData($"/api/organizations/{OrgId}/prepaid/credits');alert(1);//", false)]
    [InlineData($"/api/organizations/{OrgId}/prepaid/credits/extra", false)]
    [InlineData($"/api/organizations/{OrgId}/completion", false)]
    [InlineData($"/api/organizations/{OrgId}/usage", false)]
    [InlineData("/api/organizations/2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21c/prepaid/credits", false)] // 35 characters
    [InlineData("/api/organizations", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedCreditPath_accepts_only_the_credit_address(string? path, bool expected)
    {
        Assert.Equal(expected, ClaudeConsoleCreditScript.IsAllowedCreditPath(path));
    }

    [Fact]
    public void CreditPath_is_allowed_by_its_own_gate()
    {
        Assert.True(ClaudeConsoleCreditScript.IsAllowedCreditPath(ClaudeConsoleCreditScript.CreditPath(OrgId)));
    }

    [Fact]
    public void FetchCredits_throws_for_a_path_outside_the_allow_list()
    {
        Assert.Throws<ArgumentException>(() => ClaudeConsoleCreditScript.FetchCredits("/api/x'+alert(1)+'"));
    }

    [Fact]
    public void FetchCredits_embeds_the_path_as_a_json_string_and_only_reads()
    {
        var script = ClaudeConsoleCreditScript.FetchCredits(ClaudeConsoleCreditScript.CreditPath(OrgId));

        Assert.Contains($"fetch(\"/api/organizations/{OrgId}/prepaid/credits\"", script);
        Assert.Contains("method: 'GET'", script);
        Assert.DoesNotContain("POST", script);
    }

    [Fact]
    public void ListOrganizations_reads_only_the_fixed_list_address()
    {
        var script = ClaudeConsoleCreditScript.ListOrganizations();

        Assert.Contains("fetch('/api/organizations', {method: 'GET'", script);
        Assert.DoesNotContain("POST", script);
        Assert.DoesNotContain("completion", script);
    }
}
