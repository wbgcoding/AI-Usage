using AiUsage.Services;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class QuietDayViewModelTests
{
    private static void InLanguage(string language, Action body)
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage(language);
        try
        {
            body();
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Theory]
    [InlineData("de", DayOfWeek.Monday, "Montag")]
    [InlineData("de", DayOfWeek.Sunday, "Sonntag")]
    [InlineData("en", DayOfWeek.Monday, "Monday")]
    [InlineData("en", DayOfWeek.Saturday, "Saturday")]
    [InlineData("fr", DayOfWeek.Monday, "Lundi")]
    [InlineData("es", DayOfWeek.Wednesday, "Miércoles")]
    public void The_name_is_the_day_in_the_active_language_with_a_capital_first_letter(string language, DayOfWeek day, string expected)
    {
        InLanguage(language, () => Assert.Equal(expected, QuietDayViewModel.NameOf(day)));
    }

    [Fact]
    public void Every_day_has_a_non_empty_name_starting_with_a_letter_that_is_not_lower_case()
    {
        InLanguage("en", () =>
        {
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                var name = QuietDayViewModel.NameOf(day);
                Assert.NotEmpty(name);
                Assert.False(char.IsLower(name[0]));
            }
        });
    }

    [Fact]
    public void The_constructor_sets_day_name_and_state_without_calling_back()
    {
        InLanguage("de", () =>
        {
            var calls = 0;

            var day = new QuietDayViewModel(DayOfWeek.Friday, true, (_, _) => calls++);

            Assert.Equal(DayOfWeek.Friday, day.Day);
            Assert.Equal("Freitag", day.Name);
            Assert.True(day.IsAllowed);
            Assert.Equal(0, calls);
        });
    }

    [Fact]
    public void Changing_the_state_reports_the_day_and_the_new_value_once_per_change()
    {
        var calls = new List<(DayOfWeek Day, bool Allowed)>();
        var day = new QuietDayViewModel(DayOfWeek.Tuesday, true, (d, allowed) => calls.Add((d, allowed)));

        day.IsAllowed = false;
        day.IsAllowed = false;
        day.IsAllowed = true;

        Assert.Equal([(DayOfWeek.Tuesday, false), (DayOfWeek.Tuesday, true)], calls);
    }

    [Fact]
    public void A_change_raises_a_property_notification_for_the_state()
    {
        var day = new QuietDayViewModel(DayOfWeek.Monday, false, (_, _) => { });
        var raised = new List<string?>();
        day.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        day.IsAllowed = true;

        Assert.Contains(nameof(QuietDayViewModel.IsAllowed), raised);
    }

    [Fact]
    public void Refreshing_the_name_follows_a_language_switch()
    {
        InLanguage("de", () =>
        {
            var day = new QuietDayViewModel(DayOfWeek.Thursday, true, (_, _) => { });
            Assert.Equal("Donnerstag", day.Name);
            var raised = new List<string?>();
            day.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            LocalizationService.Instance.SetLanguage("en");
            Assert.Equal("Donnerstag", day.Name);
            day.RefreshName();

            Assert.Equal("Thursday", day.Name);
            Assert.Equal([nameof(QuietDayViewModel.Name)], raised);
        });
    }

    [Fact]
    public void Refreshing_the_name_never_reports_a_state_change()
    {
        var calls = 0;
        var day = new QuietDayViewModel(DayOfWeek.Monday, true, (_, _) => calls++);

        day.RefreshName();

        Assert.Equal(0, calls);
    }
}
