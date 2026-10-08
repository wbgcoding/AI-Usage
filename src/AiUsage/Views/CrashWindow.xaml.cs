using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AiUsage.Services;

namespace AiUsage.Views;

/// <summary>
/// Plain-text crash dialog shown from a global unhandled-exception handler.
/// Never a native Windows crash dialog: the details are always readable and
/// copyable, and the log folder is one click away.
/// </summary>
public partial class CrashWindow : Window
{
    private readonly string _details;
    private readonly string _logFolder;

    public CrashWindow(string details, string logFolder)
    {
        InitializeComponent();
        SeedFallbackColoursIfThemeMissing();
        // The opacity setting itself may not have loaded yet (a crash this early falls back to
        // WindowOpacity's own default) - this dialog must never throw on the way to reporting
        // another crash.
        WindowChromeNative.Bootstrap(this);
        _details = details;
        _logFolder = logFolder;

        var loc = LocalizationService.Instance;
        Title = loc["Crash.Title"];
        TitleBarControl.TitleText = Title;
        HeadlineText.Text = loc["Crash.Headline"];
        MessageText.Text = loc["Crash.Message"];
        DetailsBox.Text = details;

        // Esc closes dialogs, keyboard-only throughout.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>
    /// This dialog can appear before or instead of a working theme - a failure while a theme
    /// dictionary is being swapped in is exactly the kind of crash it exists for. Every resource the
    /// XAML uses is looked up dynamically, so a missing dictionary would leave the window unpainted
    /// or unframed; each fallback below goes into the window's own resources only when nothing else
    /// answers for that key, since the colour, token and style dictionaries can fail independently.
    /// </summary>
    private void SeedFallbackColoursIfThemeMissing()
    {
        SeedIfMissing("Bg.Base", new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)));
        SeedIfMissing("Bg.Window", new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)));
        SeedIfMissing("Bg.Surface", new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x23)));
        SeedIfMissing("Bg.Raised", new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x2A)));
        SeedIfMissing("Border.Window", new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x10)));
        SeedIfMissing("Text.Primary", new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF5)));
        SeedIfMissing("Text.Secondary", new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xBC)));
        SeedIfMissing("Shadow", Color.FromArgb(0x80, 0, 0, 0));
        SeedIfMissing("Font.Ui", new FontFamily("Segoe UI"));
        SeedIfMissing("Font.Mono", new FontFamily("Consolas, Courier New"));
        SeedIfMissing("Font.Size.Micro", 10.0);
        SeedIfMissing("Font.Size.Body", 12.0);
        SeedIfMissing("Font.Size.Title", 14.0);
        SeedIfMissing("Space.Xl", new Thickness(24));

        // Without its frame style the dialog would be bare text on a transparent window. The buttons
        // need no such fallback: without their style they still draw as plain system buttons.
        if (TryFindResource("WindowChrome") is null)
        {
            var chrome = new Style(typeof(Border));
            chrome.Setters.Add(new Setter(MarginProperty, new Thickness(8)));
            chrome.Setters.Add(new Setter(Border.BackgroundProperty, FindResource("Bg.Window")));
            chrome.Setters.Add(new Setter(Border.BorderBrushProperty, FindResource("Border.Window")));
            chrome.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
            chrome.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(8)));
            Resources["WindowChrome"] = chrome;
        }
    }

    private void SeedIfMissing(string key, object value)
    {
        if (TryFindResource(key) is null)
            Resources[key] = value;
    }

    private void TitleBarControl_CloseRequested(object? sender, EventArgs e) => Close();

    private void CopyDetails_Click(object sender, RoutedEventArgs e) => ClipboardHelper.SetTextSafely(_details);

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_logFolder);
            Process.Start(new ProcessStartInfo(_logFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Folder not creatable, or no shell handler registered for it - nothing sensible to recover into.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
