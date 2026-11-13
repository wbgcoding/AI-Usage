using AiUsage.Models;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Which window the tray follows once the chosen one is no longer shown: the next one in the
/// order five-hour, weekly, other rows, else the nearest before it.</summary>
public class TrayWindowFallbackTests
{
    private static readonly (WindowKind, string) FiveHour = (WindowKind.FiveHour, "Window_FiveHour");
    private static readonly (WindowKind, string) Weekly = (WindowKind.Weekly, "Window_Weekly");
    private static readonly (WindowKind, string) Opus = (WindowKind.Other, "Window_Opus");
    private static readonly (WindowKind, string) Sonnet = (WindowKind.Other, "Window_Sonnet");

    [Theory]
    [InlineData(null)]
    [InlineData("Auto")]
    [InlineData("Other")]
    public void AutomaticAndTheLegacyOtherValuePassThrough(string? stored) =>
        Assert.Equal(stored, MainViewModel.ResolveTrayWindow(stored, [Weekly]));

    [Fact]
    public void AShownWindowStaysChosen()
    {
        Assert.Equal("FiveHour", MainViewModel.ResolveTrayWindow("FiveHour", [FiveHour, Weekly]));
        Assert.Equal("Label:Window_Opus", MainViewModel.ResolveTrayWindow("Label:Window_Opus", [Weekly, Opus]));
    }

    [Fact]
    public void AMissingFiveHourWindowMovesOnToTheWeeklyOneBeforeAnyOtherRow() =>
        Assert.Equal("Weekly", MainViewModel.ResolveTrayWindow("FiveHour", [Opus, Weekly]));

    [Fact]
    public void AMissingFiveHourWindowWithoutAWeeklyOneMovesOnToTheFirstOtherRow() =>
        Assert.Equal("Label:Window_Opus", MainViewModel.ResolveTrayWindow("FiveHour", [Opus, Sonnet]));

    [Fact]
    public void AMissingWeeklyWindowMovesOnToTheFirstOtherRowElseBackToFiveHour()
    {
        Assert.Equal("Label:Window_Sonnet", MainViewModel.ResolveTrayWindow("Weekly", [FiveHour, Sonnet, Opus]));
        Assert.Equal("FiveHour", MainViewModel.ResolveTrayWindow("Weekly", [FiveHour]));
    }

    [Fact]
    public void AMissingOtherRowMovesOnToAnotherOtherRowElseTheWeeklyOne()
    {
        Assert.Equal("Label:Window_Sonnet", MainViewModel.ResolveTrayWindow("Label:Window_Opus", [FiveHour, Weekly, Sonnet]));
        Assert.Equal("Weekly", MainViewModel.ResolveTrayWindow("Label:Window_Opus", [FiveHour, Weekly]));
        Assert.Equal("FiveHour", MainViewModel.ResolveTrayWindow("Label:Window_Opus", [FiveHour]));
    }

    [Fact]
    public void NothingShownLeavesNoWindowToFollow() =>
        Assert.Null(MainViewModel.ResolveTrayWindow("FiveHour", []));

    [Fact]
    public void AnUnknownStoredValuePassesThrough() =>
        Assert.Equal("Monthly", MainViewModel.ResolveTrayWindow("Monthly", [Weekly]));
}
