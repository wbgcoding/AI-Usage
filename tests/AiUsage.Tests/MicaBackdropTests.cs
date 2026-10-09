using System.Text.Json;
using System.Windows.Media;
using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Tests;

public class MicaBackdropTests
{
    private const int Windows11 = 22631;

    [Fact]
    public void Applies_in_theme_System_on_windows_11_when_opaque_and_enabled() =>
        Assert.True(MicaPolicy.ShouldApply(Windows11, AppTheme.System, 100, false, true));

    [Fact]
    public void A_windows_10_build_never_gets_mica() =>
        Assert.False(MicaPolicy.ShouldApply(19045, AppTheme.System, 100, false, true));

    [Fact]
    public void The_first_windows_11_builds_without_the_backdrop_attribute_do_not_get_mica() =>
        Assert.False(MicaPolicy.ShouldApply(22000, AppTheme.System, 100, false, true));

    [Fact]
    public void A_translucent_window_gets_no_mica() =>
        Assert.False(MicaPolicy.ShouldApply(Windows11, AppTheme.System, 90, false, true));

    [Fact]
    public void High_contrast_gets_no_mica() =>
        Assert.False(MicaPolicy.ShouldApply(Windows11, AppTheme.System, 100, true, true));

    [Fact]
    public void Disabled_gets_no_mica() =>
        Assert.False(MicaPolicy.ShouldApply(Windows11, AppTheme.System, 100, false, false));

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Nebula)]
    [InlineData(AppTheme.Terminal)]
    public void Only_theme_System_gets_mica(AppTheme theme) =>
        Assert.False(MicaPolicy.ShouldApply(Windows11, theme, 100, false, true));

    [Fact]
    public void Cards_keep_their_color_at_88_percent_opacity()
    {
        var card = MicaBackdrop.CardColor(Color.FromRgb(0x20, 0x20, 0x23));

        Assert.Equal(Color.FromArgb(0xE0, 0x20, 0x20, 0x23), card);
        Assert.InRange(card.A / 255.0, 0.87, 0.89);
    }

    [Fact]
    public void MicaEnabled_is_on_by_default()
    {
        Assert.True(new AppSettings().MicaEnabled);
        Assert.True(JsonSerializer.Deserialize<AppSettings>("{}")!.MicaEnabled);
        Assert.False(JsonSerializer.Deserialize<AppSettings>("{\"MicaEnabled\": false}")!.MicaEnabled);
    }

    private sealed class FakeDwm : IDwmBackdropApi
    {
        public int BackdropResult { get; set; }
        public int DarkResult { get; set; }
        public int FrameResult { get; set; }
        public bool Throws { get; set; }
        public List<string> Calls { get; } = [];

        public int SetBackdropType(IntPtr hwnd, int type)
        {
            Calls.Add($"backdrop {type}");
            if (Throws)
                throw new DllNotFoundException("dwmapi");
            return BackdropResult;
        }

        public int SetDarkMode(IntPtr hwnd, bool dark)
        {
            Calls.Add($"dark {dark}");
            return DarkResult;
        }

        public int ExtendFrame(IntPtr hwnd, bool intoWholeWindow)
        {
            Calls.Add($"frame {intoWholeWindow}");
            return FrameResult;
        }
    }

    private static (MicaController Controller, FakeDwm Dwm, List<bool> Transparent, List<string> Log) Build()
    {
        var dwm = new FakeDwm();
        var transparent = new List<bool>();
        var log = new List<string>();
        return (new MicaController(IntPtr.Zero, dwm, transparent.Add, log.Add), dwm, transparent, log);
    }

    [Fact]
    public void All_calls_succeeding_makes_the_background_transparent()
    {
        var (controller, dwm, transparent, log) = Build();

        var active = controller.Update(wanted: true, dark: true);

        Assert.True(active);
        Assert.True(controller.IsActive);
        Assert.True(transparent[^1]);
        Assert.Contains("backdrop 2", dwm.Calls);
        Assert.Contains("frame True", dwm.Calls);
        Assert.Contains("dark True", dwm.Calls);
        Assert.Empty(log);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public void A_failing_call_restores_the_opaque_background_and_undoes_the_frame(int dark, int backdrop, int frame)
    {
        var (controller, dwm, transparent, log) = Build();
        dwm.DarkResult = dark;
        dwm.BackdropResult = backdrop;
        dwm.FrameResult = frame;

        var active = controller.Update(wanted: true, dark: false);

        Assert.False(active);
        Assert.False(controller.IsActive);
        Assert.False(transparent[^1]);
        Assert.DoesNotContain(true, transparent);
        Assert.Contains("frame False", dwm.Calls);
        Assert.Contains("backdrop 1", dwm.Calls);
        Assert.Single(log);
    }

    [Fact]
    public void A_throwing_call_is_treated_like_a_failed_one()
    {
        var (controller, dwm, transparent, log) = Build();
        dwm.Throws = true;

        var active = controller.Update(wanted: true, dark: false);

        Assert.False(active);
        Assert.DoesNotContain(true, transparent);
        Assert.Single(log);
    }

    [Fact]
    public void A_failure_is_logged_only_once()
    {
        var (controller, dwm, _, log) = Build();
        dwm.FrameResult = unchecked((int)0x80004005);

        controller.Update(true, false);
        controller.Update(true, false);
        controller.Update(true, true);

        Assert.Single(log);
    }

    [Fact]
    public void A_later_change_tries_again_after_a_failure()
    {
        var (controller, dwm, transparent, _) = Build();
        dwm.FrameResult = 1;
        controller.Update(true, false);
        dwm.FrameResult = 0;

        var active = controller.Update(true, true);

        Assert.True(active);
        Assert.True(transparent[^1]);
    }

    [Fact]
    public void Switching_it_off_removes_the_backdrop_and_restores_the_background()
    {
        var (controller, dwm, transparent, _) = Build();
        controller.Update(true, false);
        dwm.Calls.Clear();

        var active = controller.Update(wanted: false, dark: false);

        Assert.False(active);
        Assert.False(controller.IsActive);
        Assert.False(transparent[^1]);
        Assert.Contains("backdrop 1", dwm.Calls);
        Assert.Contains("frame False", dwm.Calls);
    }

    [Fact]
    public void Staying_off_makes_no_window_manager_call()
    {
        var (controller, dwm, transparent, _) = Build();

        controller.Update(wanted: false, dark: false);

        Assert.Empty(dwm.Calls);
        Assert.False(transparent[^1]);
    }
}

public class MicaOptionVisibilityTests
{
    [Theory]
    [InlineData(22631, "System", true)]
    [InlineData(22631, "system", true)]
    [InlineData(22631, "Dark", false)]
    [InlineData(22631, "Nebula", false)]
    [InlineData(19045, "System", false)]
    [InlineData(22000, "System", false)]
    public void The_option_shows_only_in_theme_System_on_a_supporting_build(int build, string theme, bool expected) =>
        Assert.Equal(expected, AiUsage.ViewModels.SettingsViewModel.IsMicaOptionVisible(build, theme));
}
