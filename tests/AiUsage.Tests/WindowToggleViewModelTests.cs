using AiUsage.Models;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

public class WindowToggleViewModelTests
{
    [Theory]
    [InlineData("Label.FiveHour", WindowKind.FiveHour, "5 hours", true)]
    [InlineData("Label.Weekly", WindowKind.Weekly, "Week", false)]
    [InlineData("Premium requests", WindowKind.Other, "Premium requests", true)]
    public void The_constructor_keeps_every_value_as_given(string label, WindowKind kind, string text, bool visible)
    {
        var toggle = new WindowToggleViewModel(label, kind, text, visible);

        Assert.Equal(label, toggle.Label);
        Assert.Equal(kind, toggle.Kind);
        Assert.Equal(text, toggle.DisplayText);
        Assert.Equal(visible, toggle.IsVisible);
    }

    [Fact]
    public void Changing_the_display_text_raises_one_change_notification_and_keeps_the_label()
    {
        var toggle = new WindowToggleViewModel("Label.Weekly", WindowKind.Weekly, "Weekly", true);
        var raised = new List<string?>();
        toggle.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        toggle.DisplayText = "Woche";

        Assert.Equal([nameof(WindowToggleViewModel.DisplayText)], raised);
        Assert.Equal("Label.Weekly", toggle.Label);
    }

    [Fact]
    public void Setting_the_same_display_text_raises_nothing()
    {
        var toggle = new WindowToggleViewModel("x", WindowKind.Other, "same", true);
        var raised = 0;
        toggle.PropertyChanged += (_, _) => raised++;

        toggle.DisplayText = "same";

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Switching_the_visibility_raises_a_notification_only_on_a_real_change()
    {
        var toggle = new WindowToggleViewModel("x", WindowKind.Other, "x", true);
        var raised = new List<string?>();
        toggle.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        toggle.IsVisible = true;
        toggle.IsVisible = false;
        toggle.IsVisible = false;
        toggle.IsVisible = true;

        Assert.Equal([nameof(WindowToggleViewModel.IsVisible), nameof(WindowToggleViewModel.IsVisible)], raised);
    }

    [Fact]
    public void Empty_texts_are_accepted()
    {
        var toggle = new WindowToggleViewModel("", WindowKind.Other, "", false);

        Assert.Equal("", toggle.Label);
        Assert.Equal("", toggle.DisplayText);
        Assert.False(toggle.IsVisible);
    }
}
