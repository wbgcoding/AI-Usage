using System.Text.Json;
using AiUsage.Models;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public sealed class StatusFileTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly DisposableTestDirectory _directory = TestPaths.CreateDisposableDirectory("ai-usage-status-file");

    public void Dispose() => _directory.Dispose();

    private static StatusEntry Claude(string? account = null, ProviderStatus status = ProviderStatus.Ok) => new(
        "claude", "Claude", account, status,
        [
            new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, Now.AddHours(2).AddMinutes(14), 300, new TokenUsage(1823457)),
            new UsageWindow("Window_Weekly", WindowKind.Weekly, 63, Now.AddDays(3).AddHours(4), 10080),
        ]);

    private static StatusEntry Codex(ProviderStatus status = ProviderStatus.Ok) => new(
        "codex", "Codex", null, status, [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 7, Now.AddMinutes(45), 300)]);

    [Fact]
    public void TheDocumentHasTheDocumentedShape()
    {
        var json = StatusFileWriter.BuildJson([Claude("Work")], Now);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(["version", "updated", "providers"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(Now, DateTimeOffset.Parse(root.GetProperty("updated").GetString()!));

        var provider = root.GetProperty("providers").EnumerateArray().Single();
        Assert.Equal(["id", "name", "account", "status", "windows"], provider.EnumerateObject().Select(p => p.Name));
        Assert.Equal("claude", provider.GetProperty("id").GetString());
        Assert.Equal("Claude", provider.GetProperty("name").GetString());
        Assert.Equal("Work", provider.GetProperty("account").GetString());
        Assert.Equal("ok", provider.GetProperty("status").GetString());

        var windows = provider.GetProperty("windows").EnumerateArray().ToList();
        Assert.Equal(2, windows.Count);
        Assert.Equal(["kind", "label", "usedPercent", "resetsAt", "tokens"], windows[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("fiveHour", windows[0].GetProperty("kind").GetString());
        Assert.Equal(42, windows[0].GetProperty("usedPercent").GetInt32());
        Assert.Equal(Now.AddHours(2).AddMinutes(14), DateTimeOffset.Parse(windows[0].GetProperty("resetsAt").GetString()!));
        Assert.Equal(1823457, windows[0].GetProperty("tokens").GetInt64());
        Assert.Equal("weekly", windows[1].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, windows[1].GetProperty("tokens").ValueKind);
    }

    [Fact]
    public void AMissingAccountNameAndResetTimeAreNull()
    {
        var entry = new StatusEntry("x", "X", null, ProviderStatus.Ok, [new UsageWindow("Other", WindowKind.Other, 1, null, null)]);

        using var document = JsonDocument.Parse(StatusFileWriter.BuildJson([entry], Now));

        var provider = document.RootElement.GetProperty("providers")[0];
        Assert.Equal(JsonValueKind.Null, provider.GetProperty("account").ValueKind);
        Assert.Equal(JsonValueKind.Null, provider.GetProperty("windows")[0].GetProperty("resetsAt").ValueKind);
        Assert.Equal("other", provider.GetProperty("windows")[0].GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData(ProviderStatus.Ok, "ok")]
    [InlineData(ProviderStatus.Stale, "stale")]
    [InlineData(ProviderStatus.Failed, "failed")]
    [InlineData(ProviderStatus.NotSignedIn, "signin")]
    [InlineData(ProviderStatus.Blocked, "signin")]
    [InlineData(ProviderStatus.SourceUnavailable, "unavailable")]
    [InlineData(ProviderStatus.NoLocalData, "unavailable")]
    [InlineData(ProviderStatus.RuntimeMissing, "unavailable")]
    public void EveryProviderStateMapsToOneStatusWord(ProviderStatus status, string word) =>
        Assert.Equal(word, StatusFileWriter.StatusWord(status));

    [Fact]
    public void TwoProvidersPrintOneLineEach()
    {
        var json = StatusFileWriter.BuildJson([Claude(), Codex()], Now);

        var lines = StatusCommand.FormatLines(json, Now);

        Assert.Equal(["Claude 42% 5h (2h 14m) · 63% week (3d 4h)", "Codex 7% 5h (45m)"], lines);
    }

    [Fact]
    public void AnOwnAccountNameFollowsTheProviderName()
    {
        var lines = StatusCommand.FormatLines(StatusFileWriter.BuildJson([Claude("Work")], Now), Now);

        Assert.StartsWith("Claude (Work) 42% 5h", Assert.Single(lines!));
    }

    [Fact]
    public void AProviderWithoutNumbersSaysWhatToDo()
    {
        var signedOut = new StatusEntry("codex", "Codex", null, ProviderStatus.NotSignedIn, []);
        var failed = Claude(status: ProviderStatus.Failed);

        var lines = StatusCommand.FormatLines(StatusFileWriter.BuildJson([signedOut, failed], Now), Now);

        Assert.Equal(["Codex: sign in needed", "Claude 42% 5h (2h 14m) · 63% week (3d 4h) [failed]"], lines);
    }

    [Fact]
    public void AnOldFileSaysHowOldItIs()
    {
        var json = StatusFileWriter.BuildJson([Codex()], Now);

        var lines = StatusCommand.FormatLines(json, Now.AddHours(3).AddMinutes(5));

        Assert.Equal("Codex 7% 5h · data 3h 5m old", Assert.Single(lines!));
    }

    [Fact]
    public void AFreshFileAddsNoAgeNote()
    {
        var lines = StatusCommand.FormatLines(StatusFileWriter.BuildJson([Codex()], Now), Now.AddMinutes(3));

        Assert.DoesNotContain("old", Assert.Single(lines!));
    }

    [Fact]
    public void AMissingFileExitsWithOneAndSaysSo()
    {
        var output = new StringWriter();

        var exitCode = StatusCommand.Run(["--status"], _directory.Path, Now, output);

        Assert.Equal(1, exitCode);
        Assert.Equal("AI-Usage has not written a status yet.", output.ToString().Trim());
    }

    [Fact]
    public void TheStatusSwitchPrintsTheFileLinesAndExitsWithZero()
    {
        File.WriteAllText(StatusFileWriter.PathIn(_directory.Path), StatusFileWriter.BuildJson([Claude(), Codex()], Now));
        var output = new StringWriter();

        var exitCode = StatusCommand.Run(["--status"], _directory.Path, Now, output);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            ["Claude 42% 5h (2h 14m) · 63% week (3d 4h)", "Codex 7% 5h (45m)"],
            output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void TheJsonSwitchPrintsTheFileItself()
    {
        var json = StatusFileWriter.BuildJson([Codex()], Now);
        File.WriteAllText(StatusFileWriter.PathIn(_directory.Path), json);
        var output = new StringWriter();

        var exitCode = StatusCommand.Run(["--status", "--json"], _directory.Path, Now, output);

        Assert.Equal(0, exitCode);
        Assert.Equal(json, output.ToString().TrimEnd());
    }

    [Fact]
    public void AnUnreadableFileExitsWithOne()
    {
        File.WriteAllText(StatusFileWriter.PathIn(_directory.Path), "{ not json");
        var output = new StringWriter();

        var exitCode = StatusCommand.Run(["--status"], _directory.Path, Now, output);

        Assert.Equal(1, exitCode);
        Assert.Equal(StatusCommand.UnreadableMessage, output.ToString().Trim());
    }

    [Theory]
    [InlineData("--status", true)]
    [InlineData("--STATUS", true)]
    [InlineData("--tray", false)]
    public void OnlyTheStatusSwitchIsRecognised(string arg, bool expected) =>
        Assert.Equal(expected, StatusCommand.IsRequested([arg]));

    [Theory]
    [InlineData(20, "<1m")]
    [InlineData(9 * 60, "9m")]
    [InlineData(3600, "1h")]
    [InlineData(2 * 3600 + 14 * 60, "2h 14m")]
    [InlineData(24 * 3600, "1d")]
    [InlineData(3 * 86400 + 4 * 3600 + 30 * 60, "3d 4h")]
    public void ASpanIsWrittenInTheTwoLargestUnits(int seconds, string expected) =>
        Assert.Equal(expected, StatusCommand.FormatSpan(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void AReaderNeverSeesAHalfWrittenFile()
    {
        var path = StatusFileWriter.PathIn(_directory.Path);
        StatusFileWriter.WriteAtomic(path, StatusFileWriter.BuildJson([Claude()], Now));
        var big = StatusFileWriter.BuildJson(Enumerable.Repeat(Claude("Work"), 200), Now);
        var small = StatusFileWriter.BuildJson([Codex()], Now);
        using var stop = new CancellationTokenSource();
        var failures = 0;
        var reads = 0;

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var document = JsonDocument.Parse(stream);
                    reads++;
                }
                catch (JsonException)
                {
                    failures++;
                }
                catch (IOException)
                {
                    // The swap itself may briefly refuse an open; a half document is the failure that counts.
                }
            }
        });

        for (var i = 0; i < 200; i++)
            StatusFileWriter.WriteAtomic(path, i % 2 == 0 ? big : small);
        stop.Cancel();
        reader.Wait();

        Assert.Equal(0, failures);
        Assert.True(reads > 0);
        Assert.Empty(Directory.GetFiles(_directory.Path, "*.tmp"));
    }

    [Fact]
    public void SchedulingSeveralTimesWritesOnlyTheNewestDocument()
    {
        var path = StatusFileWriter.PathIn(_directory.Path);
        using var writer = new StatusFileWriter(() => path, TimeSpan.FromHours(1));

        writer.Schedule("first");
        writer.Schedule("second");
        Assert.False(File.Exists(path));
        writer.Flush();

        Assert.Equal("second", File.ReadAllText(path));
    }

    [Fact]
    public void DisposingWritesWhatIsStillPending()
    {
        var path = StatusFileWriter.PathIn(_directory.Path);
        var writer = new StatusFileWriter(() => path, TimeSpan.FromHours(1));
        writer.Schedule("last words");

        writer.Dispose();

        Assert.Equal("last words", File.ReadAllText(path));
    }

    [Fact]
    public void ADebouncedWriteLandsOnItsOwn()
    {
        var path = StatusFileWriter.PathIn(_directory.Path);
        using var writer = new StatusFileWriter(() => path, TimeSpan.FromMilliseconds(30));

        writer.Schedule("auto");

        SpinWait.SpinUntil(() => File.Exists(path), TimeSpan.FromSeconds(10));
        Assert.Equal("auto", File.ReadAllText(path));
    }

    [Fact]
    public void AFolderThatCannotBeWrittenCostsOnlyThatUpdate()
    {
        var blocker = Path.Combine(_directory.Path, "blocked");
        File.WriteAllText(blocker, "a file where the folder should be");
        var logged = new List<string>();
        using var writer = new StatusFileWriter(() => Path.Combine(blocker, "status.json"), TimeSpan.FromHours(1), logged.Add);

        writer.Schedule("x");
        writer.Flush();

        Assert.Single(logged);
    }
}
