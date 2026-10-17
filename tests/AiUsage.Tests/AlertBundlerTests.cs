using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class AlertBundlerTests
{
    private sealed class FakeSchedule
    {
        public List<(TimeSpan Delay, Action Action, Handle Handle)> Timers { get; } = [];

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var handle = new Handle();
            Timers.Add((delay, action, handle));
            return handle;
        }

        /// <summary>Fires every timer that was not cancelled, like the clock passing the window.</summary>
        public void Elapse()
        {
            foreach (var timer in Timers.ToList().Where(t => !t.Handle.Disposed))
                timer.Action();
        }
    }

    private sealed class Handle : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private static readonly (string, string) Show = ("Show widget", "show");
    private static readonly (string, string) Mute = ("Not again today", "mute:codex");
    private static readonly (string, string) SignIn = ("Sign in", "signin:codex");

    private static Alert A(string key, string line, params (string, string)[] buttons) =>
        new(key, key + " title", [line], buttons);

    private static (AlertBundler Bundler, FakeSchedule Schedule, List<Alert> Shown) Make()
    {
        var schedule = new FakeSchedule();
        var shown = new List<Alert>();
        return (new AlertBundler(TimeSpan.FromSeconds(3), shown.Add, schedule.Schedule), schedule, shown);
    }

    [Fact]
    public void NothingShowsBeforeTheWindowEnds()
    {
        var (bundler, schedule, shown) = Make();

        bundler.Add(A("codex", "5h at 90%", Show));

        Assert.Empty(shown);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(schedule.Timers).Delay);
    }

    [Fact]
    public void TwoAlertsOfOneAccountWithinTheWindowShowOnceWithBothLinesAndTheUnionOfButtons()
    {
        var (bundler, schedule, shown) = Make();

        bundler.Add(A("codex", "5h at 90%", Show, Mute));
        bundler.Add(A("codex", "week at 80%", Show, SignIn));
        schedule.Elapse();

        var alert = Assert.Single(shown);
        Assert.Equal("codex title", alert.Title);
        Assert.Equal(["5h at 90%", "week at 80%"], alert.Lines);
        Assert.Equal(["show", "signin:codex", "mute:codex"], alert.Buttons.Select(b => b.Argument));
        Assert.Single(schedule.Timers);
    }

    [Fact]
    public void DifferentAccountsShowSeparately()
    {
        var (bundler, schedule, shown) = Make();

        bundler.Add(A("codex", "one", Show));
        bundler.Add(A("claude", "two", Show));
        schedule.Elapse();

        Assert.Equal(["codex", "claude"], shown.Select(a => a.AccountKey));
        Assert.All(shown, a => Assert.Single(a.Lines));
    }

    [Fact]
    public void AnAlertAfterTheFlushStartsANewBundle()
    {
        var (bundler, schedule, shown) = Make();

        bundler.Add(A("codex", "first", Show));
        schedule.Elapse();
        bundler.Add(A("codex", "second", Show));
        schedule.Elapse();

        Assert.Equal(2, shown.Count);
        Assert.Equal(["first"], shown[0].Lines);
        Assert.Equal(["second"], shown[1].Lines);
        Assert.Equal(2, schedule.Timers.Count);
    }

    [Fact]
    public void DuplicateButtonArgumentsAreKeptOnce()
    {
        var (bundler, schedule, shown) = Make();

        bundler.Add(A("codex", "a", Show, Mute));
        bundler.Add(A("codex", "b", Show, Mute));
        schedule.Elapse();

        Assert.Equal(["show", "mute:codex"], Assert.Single(shown).Buttons.Select(b => b.Argument));
    }

    [Fact]
    public void AtMostThreeButtonsSurviveInTheFixedOrder()
    {
        var (bundler, schedule, shown) = Make();

        bundler.Add(A("codex", "a", Mute, ("Other", "other"), SignIn));
        bundler.Add(A("codex", "b", Show));
        schedule.Elapse();

        Assert.Equal(["show", "signin:codex", "mute:codex"], Assert.Single(shown).Buttons.Select(b => b.Argument));
    }

    [Fact]
    public void FlushAllShowsPendingBundlesAndCancelsTheirTimers()
    {
        var (bundler, schedule, shown) = Make();

        bundler.Add(A("codex", "one", Show));
        bundler.Add(A("claude", "two", Show));
        bundler.FlushAll();
        schedule.Elapse();

        Assert.Equal(2, shown.Count);
        Assert.All(schedule.Timers, t => Assert.True(t.Handle.Disposed));
    }

    [Fact]
    public void FlushAllWithNothingPendingShowsNothing()
    {
        var (bundler, _, shown) = Make();

        bundler.FlushAll();

        Assert.Empty(shown);
    }
}
