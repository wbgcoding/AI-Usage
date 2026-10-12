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

    [Theory]
    [InlineData(FailureKind.ServerError, "Status_Failed_Server_Headline", "Status_Failed_Server_Reason")]
    [InlineData(FailureKind.Refused, "Status_Failed_Refused_Headline", "Status_Failed_Refused_Reason")]
    [InlineData(FailureKind.RateLimited, "Status_Failed_RateLimited_Headline", "Status_Failed_RateLimited_Reason")]
    [InlineData(FailureKind.Timeout, "Status_Failed_Timeout_Headline", "Status_Failed_Timeout_Reason")]
    [InlineData(FailureKind.Network, "Status_Failed_Network_Headline", "Status_Failed_Network_Reason")]
    [InlineData(FailureKind.Other, "Status_Failed_Headline", "Status_Failed_Reason")]
    public void Describe_names_each_failure_kind_in_its_own_words(FailureKind kind, string headlineKey, string reasonKey)
    {
        var error = new ProviderError("Status_Failed_Reason", "Action_Retry", Kind: kind);

        var (headline, reason, _) = ErrorPresenter.Describe(ProviderStatus.Failed, error);

        Assert.Equal(headlineKey, headline);
        Assert.Equal(reasonKey, reason);
    }

    [Theory]
    [InlineData(502, FailureKind.ServerError)]
    [InlineData(500, FailureKind.ServerError)]
    [InlineData(404, FailureKind.Refused)]
    [InlineData(429, FailureKind.RateLimited)]
    [InlineData(401, FailureKind.Refused)]
    [InlineData(0, FailureKind.Other)]
    public void KindForStatus_splits_server_errors_from_refusals(int status, FailureKind expected) =>
        Assert.Equal(expected, ProviderError.KindForStatus(status));

    [Fact]
    public void A_404_reads_as_an_unreadable_answer_not_as_a_refusal()
    {
        var error = new ProviderError("Status_Failed_Reason", Kind: FailureKind.Refused, HttpStatus: 404);

        var (headline, reason, action) = ErrorPresenter.Describe(ProviderStatus.Failed, error);

        Assert.Equal("Status_Failed_Headline", headline);
        Assert.Equal("Status_UnreadableAnswer_Reason", reason);
        Assert.Null(action);
        Assert.Equal(
            LocalizationService.Instance["State.UnreadableAnswer.Reason"],
            StatusTextMap.ResolveFailure(reason, "Claude", 404));
    }

    [Fact]
    public void A_429_names_the_request_limit_and_the_status()
    {
        var error = new ProviderError("Status_Failed_Reason", Kind: FailureKind.RateLimited, HttpStatus: 429);

        var (headline, reason, action) = ErrorPresenter.Describe(ProviderStatus.Failed, error);

        Assert.Null(action);
        Assert.Equal(
            string.Format(LocalizationService.Instance["State.Failed.RateLimited.Head"], "Claude", 429),
            StatusTextMap.ResolveFailure(headline, "Claude", 429));
        Assert.Contains("429", StatusTextMap.ResolveFailure(reason, "Claude", 429), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FailureKind.RateLimited, true)]
    [InlineData(FailureKind.ServerError, true)]
    [InlineData(FailureKind.Timeout, true)]
    [InlineData(FailureKind.Network, true)]
    [InlineData(FailureKind.Refused, false)]
    [InlineData(FailureKind.Other, false)]
    public void Only_the_kinds_that_sign_in_cannot_fix_hide_the_sign_in_button(FailureKind kind, bool notASignInProblem) =>
        Assert.Equal(notASignInProblem, ErrorPresenter.IsNotASignInProblem(kind));
}
