using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// The attached tooltip of a trimming text block: present while enabled, cancelled at the moment it
/// would open unless the text is really cut off. Runs on an STA thread with an unshown text block that
/// is measured and arranged by hand.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class TrimmedToolTipTests
{
    private const string LongText = "A fairly long project name that will not fit into a narrow box";

    [Fact]
    public void The_behavior_is_off_by_default()
    {
        Sta(() =>
        {
            var text = new TextBlock { Text = "x" };

            Assert.False(TrimmedToolTip.GetEnabled(text));
            Assert.Null(text.ToolTip);
        });
    }

    [Fact]
    public void Enabling_installs_a_tooltip_that_shows_the_text_and_follows_changes_to_it()
    {
        Sta(() =>
        {
            var text = new TextBlock { Text = "first" };

            TrimmedToolTip.SetEnabled(text, true);

            var tip = Assert.IsType<ToolTip>(text.ToolTip);
            Assert.Equal("first", tip.Content);
            text.Text = "second";
            Assert.Equal("second", tip.Content);
        });
    }

    [Fact]
    public void Disabling_removes_the_tooltip_again()
    {
        Sta(() =>
        {
            var text = new TextBlock { Text = "x" };
            TrimmedToolTip.SetEnabled(text, true);

            TrimmedToolTip.SetEnabled(text, false);

            Assert.Null(text.ToolTip);
            Assert.False(TrimmedToolTip.GetEnabled(text));
        });
    }

    [Fact]
    public void An_element_that_is_not_a_text_block_gets_no_tooltip()
    {
        Sta(() =>
        {
            var button = new Button();

            TrimmedToolTip.SetEnabled(button, true);

            Assert.True(TrimmedToolTip.GetEnabled(button));
            Assert.Null(button.ToolTip);
        });
    }

    [Fact]
    public void Text_that_is_cut_off_keeps_its_tooltip_open()
    {
        Sta(() =>
        {
            var text = Arranged(LongText, 40, TextTrimming.CharacterEllipsis);

            Assert.False(Open(text).Handled);
        });
    }

    [Fact]
    public void Text_that_fits_cancels_the_tooltip()
    {
        Sta(() =>
        {
            var text = Arranged("Short", 400, TextTrimming.CharacterEllipsis);

            Assert.True(Open(text).Handled);
        });
    }

    [Fact]
    public void Text_without_trimming_cancels_the_tooltip_even_when_it_overflows()
    {
        Sta(() =>
        {
            var text = Arranged(LongText, 40, TextTrimming.None);

            Assert.True(Open(text).Handled);
        });
    }

    [Fact]
    public void Empty_text_cancels_the_tooltip()
    {
        Sta(() =>
        {
            var text = Arranged("", 40, TextTrimming.CharacterEllipsis);

            Assert.True(Open(text).Handled);
        });
    }

    [Fact]
    public void A_text_block_that_was_never_laid_out_has_zero_width_so_its_text_counts_as_cut_off()
    {
        Sta(() =>
        {
            var text = new TextBlock { Text = LongText, TextTrimming = TextTrimming.WordEllipsis };
            TrimmedToolTip.SetEnabled(text, true);

            Assert.False(Open(text).Handled);
        });
    }

    [Fact]
    public void Once_disabled_the_opening_event_is_no_longer_cancelled()
    {
        Sta(() =>
        {
            var text = Arranged("Short", 400, TextTrimming.CharacterEllipsis);
            TrimmedToolTip.SetEnabled(text, false);

            Assert.False(Open(text).Handled);
        });
    }

    [Fact]
    public void A_bigger_font_turns_a_fitting_text_into_a_cut_off_one()
    {
        Sta(() =>
        {
            var text = Arranged("Name that just fits", 400, TextTrimming.CharacterEllipsis);
            Assert.True(Open(text).Handled);

            text.FontSize = 200;
            text.Measure(new Size(400, double.PositiveInfinity));
            text.Arrange(new Rect(0, 0, 400, text.DesiredSize.Height));

            Assert.False(Open(text).Handled);
        });
    }

    private static TextBlock Arranged(string content, double width, TextTrimming trimming)
    {
        var text = new TextBlock { Text = content, TextTrimming = trimming, Width = width };
        TrimmedToolTip.SetEnabled(text, true);
        text.Measure(new Size(width, 100));
        text.Arrange(new Rect(0, 0, width, 100));
        return text;
    }

    // ToolTipEventArgs has no public constructor, so the opening event is built the way the framework does.
    private static ToolTipEventArgs Open(TextBlock text)
    {
        var args = (ToolTipEventArgs)Activator.CreateInstance(
            typeof(ToolTipEventArgs), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [true], null)!;
        text.RaiseEvent(args);
        return args;
    }

    private static void Sta(Action body)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Tooltip test did not finish");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
