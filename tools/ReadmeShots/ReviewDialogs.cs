using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using AiUsage.Views;
using AiUsage.Web;

namespace ReadmeShots;

/// <summary>The review pictures of the error texts and dialogs: tiles in the failed and the blocked
/// state, the sign-in window's blocked notice, the welcome window and the remove-account
/// confirmation. Everything is drawn offscreen from a window that is never shown, in whatever theme
/// and language the review loop has set when it calls a render method.</summary>
internal static class ReviewDialogs
{
    private const double WindowCornerRadius = 8;
    private const int ImageScale = 2;

    private const string TileChromeXaml = """
        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:controls="clr-namespace:AiUsage.Views.Controls;assembly=AI-Usage"
                Style="{StaticResource WindowChrome}" Padding="8"
                TextElement.Foreground="{DynamicResource Text.Primary}"
                RenderOptions.BitmapScalingMode="HighQuality">
            <controls:ProviderTile/>
        </Border>
        """;

    internal static void RenderFailedTile(SurfaceContext context, string id)
    {
        var now = DateTimeOffset.Now;
        RenderTile(
            new ProviderSnapshot("claude", [], null, SourceKind.None, now, null, ProviderStatus.Failed,
                new ProviderError("Status_Failed_Reason", "Action_Retry", "State.Failed.Detail.Http", "502")),
            now, context.Pick(ReviewSurfaces.WidgetWidth, ReviewSurfaces.MinWidgetWidth), context.OutputPath(id));
    }

    internal static void RenderBlockedTile(SurfaceContext context, string id)
    {
        var now = DateTimeOffset.Now;
        RenderTile(
            new ProviderSnapshot("claude", [], null, SourceKind.WebSession, now, null, ProviderStatus.Blocked, null),
            now, context.Pick(ReviewSurfaces.WidgetWidth, ReviewSurfaces.MinWidgetWidth), context.OutputPath(id));
    }

    internal static void RenderCrash(string outputPath)
    {
        // A few lines of sample details: the box keeps its minimum height and grows with them.
        var details = string.Join(Environment.NewLine, Enumerable.Range(1, 6).Select(i =>
            $"   at Sample.Namespace.Type.Method{i}(String argument) in Sample.cs:line {i * 10}"));
        var window = new CrashWindow("System.InvalidOperationException: sample" + Environment.NewLine + details, @"C:\Sample\logs");
        RenderWindowContent(window, window.Width, double.NaN, outputPath);
    }

    private static void RenderTile(ProviderSnapshot snapshot, DateTimeOffset now, double width, string outputPath)
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true, Density = TileDensity.Full };
        tile.Apply(snapshot, now);

        var chrome = (Border)XamlReader.Parse(TileChromeXaml);
        chrome.DataContext = tile;

        Pump(TimeSpan.FromMilliseconds(300));
        chrome.Measure(new Size(width, double.PositiveInfinity));
        Pump(TimeSpan.FromMilliseconds(300));
        chrome.Measure(new Size(width, double.PositiveInfinity));
        RenderToPng(chrome, width, Math.Ceiling(chrome.DesiredSize.Height), outputPath);
    }

    internal static void RenderBlockedSignIn(string outputPath)
    {
        var descriptor = new WebSessionDescriptor(
            "review", "https://example.test/", "https://example.test/login", ["example.test"], "review-sample");
        var window = new SignInWindow(descriptor);
        typeof(SignInWindow)
            .GetMethod("ShowBlockedNotice", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, ["https://accounts.example.net/login"]);
        RenderWindowContent(window, window.Width, window.Height, outputPath);
    }

    internal static void RenderWelcome(DateTimeOffset now, string outputPath)
    {
        var tiles = new[]
            {
                ("claude", "Claude", ProviderStatus.Ok),
                ("codex", "Codex", ProviderStatus.NotSignedIn),
                ("cursor", "Cursor", ProviderStatus.NotSignedIn),
                ("gemini", "Gemini", ProviderStatus.NotSignedIn),
                ("copilot", "Copilot", ProviderStatus.NoLocalData),
            }
            .Select(entry =>
            {
                var tile = new ProviderTileViewModel(entry.Item1, entry.Item2) { SupportsInAppSignIn = entry.Item1 != "copilot" };
                tile.Apply(new ProviderSnapshot(entry.Item1, [], null, SourceKind.None, now, null, entry.Item3, null), now);
                return tile;
            })
            .ToList();
        var window = new WelcomeWindow(tiles);
        RenderWindowContent(window, window.Width, double.NaN, outputPath);
    }

    internal static void RenderRemoveAccountConfirm(string outputPath)
    {
        var loc = LocalizationService.Instance;
        var window = new ConfirmWindow(
            loc["Settings.RemoveAccount"],
            loc["Settings.RemoveAccount.Confirm"],
            loc["Settings.RemoveAccount"],
            loc["Action.Cancel"],
            isDestructive: true,
            showExtraOption: true,
            extraOptionText: loc["Settings.RemoveAccount.AlsoHistory"]);
        RenderWindowContent(window, window.Width, double.NaN, outputPath);
    }

    /// <summary>Draws a never-shown window's content at its own width; a NaN height means the content
    /// decides (the window sizes to its content). The window's own background is part of the picture,
    /// as it is on screen.</summary>
    private static void RenderWindowContent(Window window, double width, double height, string outputPath)
    {
        var root = WithWindowBackground(window, (FrameworkElement)window.Content);
        Pump(TimeSpan.FromMilliseconds(300));
        root.Measure(new Size(width, double.IsNaN(height) ? double.PositiveInfinity : height));
        Pump(TimeSpan.FromMilliseconds(300));
        root.Measure(new Size(width, double.IsNaN(height) ? double.PositiveInfinity : height));
        RenderToPng(root, width, double.IsNaN(height) ? Math.Ceiling(root.DesiredSize.Height) : height, outputPath);
    }

    /// <summary>A window paints its own background behind its content; a content root that has none of
    /// its own (a plain panel) would show transparent in the picture. Such a root is put in a border
    /// carrying the window's background, which also fills the part of the window the content leaves
    /// free. A root that draws its own surface is returned as it is.</summary>
    private static FrameworkElement WithWindowBackground(Window window, FrameworkElement root)
    {
        var drawsBackground = root switch
        {
            Panel panel => panel.Background is not null,
            Border border => border.Background is not null,
            Control control => control.Background is not null,
            _ => false,
        };
        if (drawsBackground || window.Background is null)
            return root;

        window.Content = null;
        return new Border { Background = window.Background, Child = root };
    }

    private static void RenderToPng(FrameworkElement root, double width, double height, string outputPath)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        root.Clip = new RectangleGeometry(new Rect(0, 0, width, height), WindowCornerRadius, WindowCornerRadius);

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * ImageScale), (int)Math.Ceiling(height * ImageScale),
            96 * ImageScale, 96 * ImageScale, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
        Console.WriteLine($"{Path.GetFileName(outputPath)}: {bitmap.PixelWidth} x {bitmap.PixelHeight}");
    }

    private static void Pump(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
