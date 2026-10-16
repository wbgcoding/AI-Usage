using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;
using AiUsage.Providers.Parsing;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Whether Gemini shows the waiting mark, driven by conversation databases built in a temp
/// folder; the tool's own folders on this machine are never read.</summary>
public class GeminiAttentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    private readonly DisposableTestDirectory _directory = TestPaths.CreateDisposableDirectory("ai-usage-gemini-attention");

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task A_finished_answer_older_than_the_minimum_age_marks_the_tile_as_waiting()
    {
        var written = Now.UtcDateTime.AddMinutes(-10);
        Conversation("a", written, (1, 14, 3), (2, 15, 3));

        var snapshot = await Fetch(Provider());

        Assert.True(snapshot.IsWaitingForUser);
        Assert.Equal(new DateTimeOffset(written, TimeSpan.Zero), snapshot.WaitingSince);
    }

    [Fact]
    public async Task An_answer_finished_a_moment_ago_is_not_waiting_yet()
    {
        Conversation("a", Now.UtcDateTime.AddSeconds(-5), (1, 14, 3), (2, 15, 3));

        Assert.False((await Fetch(Provider())).IsWaitingForUser);
    }

    [Fact]
    public async Task A_tool_step_last_means_the_agent_is_still_working()
    {
        Conversation("a", Now.UtcDateTime.AddMinutes(-10), (1, 14, 3), (2, 15, 3), (3, 132, 3));

        var snapshot = await Fetch(Provider());

        Assert.False(snapshot.IsWaitingForUser);
        Assert.Null(snapshot.WaitingSince);
    }

    [Fact]
    public async Task A_step_that_ended_with_an_error_is_not_waiting()
    {
        Conversation("a", Now.UtcDateTime.AddMinutes(-10), (1, 14, 3), (2, 15, 7));

        Assert.False((await Fetch(Provider())).IsWaitingForUser);
    }

    [Fact]
    public async Task An_answer_older_than_the_maximum_age_is_no_longer_waiting()
    {
        Conversation("a", Now.UtcDateTime.AddHours(-3), (1, 14, 3), (2, 15, 3));

        Assert.False((await Fetch(Provider())).IsWaitingForUser);
    }

    [Fact]
    public async Task The_maximum_age_is_asked_again_on_every_read()
    {
        Conversation("a", Now.UtcDateTime.AddMinutes(-30), (1, 14, 3), (2, 15, 3));
        var maxAge = TwoHours;
        var provider = Provider(attentionMaxAge: () => maxAge);

        Assert.True((await Fetch(provider)).IsWaitingForUser);

        maxAge = TimeSpan.FromMinutes(10);
        Assert.False((await Fetch(provider)).IsWaitingForUser);
    }

    [Fact]
    public async Task A_missing_conversation_folder_is_not_waiting()
    {
        var provider = Provider(roots: [Path.Combine(_directory.Path, "nope")]);

        Assert.False((await Fetch(provider)).IsWaitingForUser);
    }

    [Fact]
    public async Task The_mark_also_shows_while_the_sign_in_read_has_no_numbers()
    {
        Conversation("a", Now.UtcDateTime.AddMinutes(-10), (1, 14, 3), (2, 15, 3));
        var provider = Provider(usage: AntigravityUsage.NotSignedIn);

        var snapshot = await Fetch(provider);

        Assert.Equal(ProviderStatus.NotSignedIn, snapshot.Status);
        Assert.True(snapshot.IsWaitingForUser);
    }

    [Fact]
    public async Task A_web_only_account_is_never_marked()
    {
        Conversation("a", Now.UtcDateTime.AddMinutes(-10), (1, 14, 3), (2, 15, 3));
        var provider = new GeminiProvider(
            fetch: null, () => Now, accountKey: "gemini#2", conversationRoots: [_directory.Path]);

        Assert.False((await Fetch(provider)).IsWaitingForUser);
    }

    [Fact]
    public void Gemini_is_one_of_the_providers_that_can_detect_a_wait() =>
        Assert.True(AttentionDetector.CanDetect("gemini"));

    private string Conversation(string id, DateTime writeTimeUtc, params (long Index, long Type, long Status)[] steps) =>
        AntigravityTurnReaderTests.CreateConversation(_directory.Path, id, writeTimeUtc, steps);

    private GeminiProvider Provider(
        AntigravityUsage? usage = null, IReadOnlyList<string>? roots = null, Func<TimeSpan>? attentionMaxAge = null) =>
        new(
            _ => Task.FromResult(usage ?? new AntigravityUsage(LocalLoginOutcome.Ok, "{}", null)),
            () => Now,
            readAccountLabel: () => null,
            attentionMaxAge: attentionMaxAge ?? (() => TwoHours),
            conversationRoots: roots ?? [_directory.Path]);

    private static Task<ProviderSnapshot> Fetch(GeminiProvider provider) => provider.FetchAsync(CancellationToken.None);
}
