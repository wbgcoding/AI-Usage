using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class ErrorPresenterTests
{
    public static IEnumerable<object[]> AllStatuses() =>
        Enum.GetValues<ProviderStatus>().Select(status => new object[] { status });

    [Theory]
    [InlineData("en", "download: HttpRequestException 0x80131500 status 404")]
    [InlineData("de", "download: HttpRequestException 0x80131500 Status 404")]
    public void TechnicalLine_names_the_http_status_in_the_active_language(string language, string expected)
    {
        LocalizationService.Instance.SetLanguage(language);
        try
        {
            var exception = new System.Net.Http.HttpRequestException(
                "boom", inner: null, statusCode: System.Net.HttpStatusCode.NotFound) { HResult = unchecked((int)0x80131500) };

            Assert.Equal(expected, ErrorPresenter.TechnicalLine("download", exception));
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Describe_fills_headline_and_reason_for_every_status(ProviderStatus status)
    {
        var (headlineKey, reasonKey, _) = ErrorPresenter.Describe(status, error: null);

        Assert.False(string.IsNullOrWhiteSpace(headlineKey));
        Assert.False(string.IsNullOrWhiteSpace(reasonKey));
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Describe_never_returns_an_exception_message(ProviderStatus status)
    {
        var error = new ProviderError("Custom_ReasonKey");
        var (_, reasonKey, _) = ErrorPresenter.Describe(status, error);

        Assert.Equal("Custom_ReasonKey", reasonKey);
        Assert.DoesNotContain("Exception", reasonKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_offers_a_fetch_now_action_for_a_stale_account_source()
    {
        var (_, _, actionKey) = ErrorPresenter.Describe(ProviderStatus.Stale, error: null, SourceKind.LocalLogin);

        Assert.Equal("Action_FetchNow", actionKey);
    }

    [Fact]
    public void Describe_offers_no_action_for_a_stale_local_file_source()
    {
        var (_, _, actionKey) = ErrorPresenter.Describe(ProviderStatus.Stale, error: null, SourceKind.LocalFile);

        Assert.Null(actionKey);
    }

    [Fact]
    public void BlockedOffersSignInForAWebProvider()
    {
        var (_, _, actionKey) = ErrorPresenter.Describe(ProviderStatus.Blocked, error: null, SourceKind.WebSession, canSignInOnWeb: true);

        Assert.Equal("Action_SignIn", actionKey);
    }

    [Fact]
    public void BlockedOffersNoActionWithoutWebSignIn()
    {
        var (_, _, actionKey) = ErrorPresenter.Describe(ProviderStatus.Blocked, error: null, SourceKind.LocalFile, canSignInOnWeb: false);

        Assert.Null(actionKey);
    }

    [Fact]
    public void UsageWindow_clamps_used_percent_below_zero()
    {
        var window = new UsageWindow("Label", WindowKind.FiveHour, usedPercent: -5, resetsAt: null, windowMinutes: null);

        Assert.Equal(0, window.UsedPercent);
    }

    [Fact]
    public void UsageWindow_clamps_used_percent_above_hundred()
    {
        var window = new UsageWindow("Label", WindowKind.Weekly, usedPercent: 150, resetsAt: null, windowMinutes: null);

        Assert.Equal(100, window.UsedPercent);
    }
}
