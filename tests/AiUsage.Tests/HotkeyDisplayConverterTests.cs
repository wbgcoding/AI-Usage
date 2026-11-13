using System.Globalization;
using AiUsage.Services;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class HotkeyDisplayConverterTests
{
    private static string Convert(params object?[] values) =>
        (string)HotkeyDisplayConverter.Instance.Convert(values!, typeof(string), null, CultureInfo.InvariantCulture);

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

    [Fact]
    public void The_stored_text_is_shown_with_the_modifiers_in_the_active_language()
    {
        InLanguage("de", () => Assert.Equal("Strg+Alt+U", Convert("Ctrl+Alt+U", "any")));
        InLanguage("en", () => Assert.Equal("Ctrl+Alt+U", Convert("Ctrl+Alt+U", "any")));
    }

    [Fact]
    public void The_second_value_only_triggers_a_refresh_and_never_changes_the_result()
    {
        InLanguage("en", () =>
        {
            Assert.Equal(Convert("Ctrl+Shift+F5"), Convert("Ctrl+Shift+F5", "x"));
            Assert.Equal(Convert("Ctrl+Shift+F5"), Convert("Ctrl+Shift+F5", null));
        });
    }

    [Fact]
    public void Text_that_does_not_parse_is_shown_as_it_is()
    {
        InLanguage("de", () => Assert.Equal("not a hotkey", Convert("not a hotkey")));
    }

    [Fact]
    public void An_empty_array_gives_an_empty_text()
    {
        Assert.Equal("", Convert());
    }

    [Fact]
    public void A_null_or_non_string_first_value_gives_an_empty_text()
    {
        Assert.Equal("", Convert((object?)null));
        Assert.Equal("", Convert(42, "x"));
        Assert.Equal("", Convert(""));
    }

    [Fact]
    public void The_shared_instance_is_a_single_object()
    {
        Assert.Same(HotkeyDisplayConverter.Instance, HotkeyDisplayConverter.Instance);
    }

    [Fact]
    public void Converting_back_is_not_supported()
    {
        Assert.Throws<NotSupportedException>(() =>
            HotkeyDisplayConverter.Instance.ConvertBack("Ctrl+U", [typeof(string), typeof(string)], null, CultureInfo.InvariantCulture));
    }
}
