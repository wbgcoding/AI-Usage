using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.Web;
using Microsoft.Web.WebView2.Core;

namespace AiUsage.Views;

/// <summary>
/// Shows the widget's own WebView2 session on a web-backed provider's real login page so the user
/// can sign in themselves. Never types credentials, never reads tokens, never writes them to its own
/// files. Window title comes from SignIn.Title.
/// </summary>
public partial class SignInWindow : Window
{
    private readonly WebViewHost _host;
    private readonly string _signInUrl;
    private readonly string _providerHost;
    private readonly IReadOnlyList<string> _allowedHosts;
    // Hosts the user has waved through on this window's own blocked notice, and the address the
    // notice is standing on. Both are dropped with the window: the next sign-in starts from the
    // provider's own list again.
    private readonly List<string> _hostsAllowedByUser = [];
    private string? _blockedUri;
    private bool _leftLoginPage;
    private bool _firstNavigationCompleted;
    private bool _closed;
    // Both feed IsAlreadySignedIn below: the moment this window opened, and whether the flow ever
    // left the provider's own host on its way through the login/OAuth path.
    private DateTimeOffset _openedAt;
    private bool _sawForeignHost;

    /// <summary>True once this window's own session came back off the login path and landed on the
    /// provider's own site again - the same moment the window decides it has finished and closes
    /// itself. It is the one thing the app can know for certain about a sign-in it cannot read the
    /// session of, so the tile takes it as "signed in" until one of its own reads says otherwise.</summary>
    public bool SignedIn { get; private set; }

    public SignInWindow(WebSessionDescriptor descriptor) : this(descriptor, new WebViewHost(descriptor))
    {
    }

    internal SignInWindow(WebSessionDescriptor descriptor, WebViewHost host)
    {
        _host = host;
        _signInUrl = descriptor.SignInUrl;
        _providerHost = new Uri(descriptor.BaseUrl).Host;
        _allowedHosts = descriptor.AllowedHosts;
        InitializeComponent();
        TitleBarControl.SetBinding(Controls.TitleBar.TitleTextProperty, NewTitleBinding());
        SetBinding(TitleProperty, NewTitleBinding());
        WindowChromeNative.Bootstrap(this);
        // Esc closes dialogs, keyboard-only throughout. While the page itself has focus the browser's
        // native window keeps the keystroke, so this answers whenever focus is on the app's own parts.
        PreviewKeyDown += (_, e) => { if (EscapeKey.ClosesWindow(e)) Close(); };
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void TitleBarControl_CloseRequested(object? sender, EventArgs e) => Close();

    /// <summary>The runtime-missing panel's own way to reach the download page - a second, weaker
    /// version of the confirm dialog <see cref="WebSignInFlow"/> already offers before this window
    /// ever opens, for the case where the runtime disappeared between that check and here.</summary>
    private void OpenDownloadPage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(WebViewAvailability.DownloadPageUrl) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No default browser registered - nothing sensible to recover into.
        }
    }

    /// <summary>Without this, the CoreWebView2 keeps this window alive through its own event
    /// subscriptions, and the WebView2 control's environment/controller lives until the process
    /// dies instead of being released the moment the sign-in dialog closes.</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        if (Browser.CoreWebView2 is { } coreWebView2)
        {
            coreWebView2.NavigationStarting -= CoreWebView2_NavigationStarting;
            coreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            coreWebView2.SourceChanged -= CoreWebView2_SourceChanged;
        }
        Browser.Dispose();
        CursorRestore.Reset();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _openedAt = DateTimeOffset.UtcNow;
        try
        {
            var environment = await _host.EnsureEnvironmentAsync();
            await Browser.EnsureCoreWebView2Async(environment);
            // The user works in this window (typing a password, clicking a consent screen), so its
            // own right-click menu stays - only the developer tools go, the same as the hidden reader
            // session this window shares a profile with.
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            Browser.CoreWebView2.SourceChanged += CoreWebView2_SourceChanged;
            // A real popup window has nowhere to render inside this minimal dialog - "Sign in with
            // Google" needs one anyway, so its content is redirected into this same, already
            // host-checked WebView2 instance instead of being silently dropped.
            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                try
                {
                    args.NewWindow = Browser.CoreWebView2;
                    args.Handled = true;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    // The opener refused to be its own popup target: load the address in it directly
                    // (NavigationStarting still applies the allow-list).
                    args.Handled = true;
                    Browser.CoreWebView2.Navigate(args.Uri);
                }
            };
            Browser.CoreWebView2.Navigate(_signInUrl);
        }
        catch (Exception ex)
        {
            if (_closed)
                return;

            LogService.Shared.LogInfo($"Sign-in window could not start the browser ({ex.GetType().Name}).");
            // The runtime check in SettingsWindow already keeps this path from being reached in the
            // common case; this is the fallback for the runtime disappearing between that check and
            // here, or for anything else EnsureCoreWebView2Async can throw. Either way the window
            // must stay alive and closable, never crash the app.
            var loc = LocalizationService.Instance;
            Browser.Visibility = Visibility.Collapsed;
            RuntimeMissingHeadText.Text = loc["State.RuntimeMissing.Head"];
            RuntimeMissingReasonText.Text = loc["State.RuntimeMissing.Reason"];
            RuntimeMissingPanel.Visibility = Visibility.Visible;
            // No NavigationCompleted will ever fire on this path - the runtime-missing panel takes
            // over messaging from here, so the loading indicator has nothing left to announce.
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Pure predicate behind <see cref="LoadingPanel"/>'s visibility, kept separate from the
    /// event handler so it is unit-testable without a real WebView2 instance (same pattern as
    /// <see cref="MainWindow.IsRefreshShortcut"/>). The loading indicator has nothing left to say once
    /// the first navigation has resolved, successfully or not - the page itself, or the runtime-missing
    /// panel, takes over from there.</summary>
    internal static bool ShouldShowLoadingIndicator(bool firstNavigationCompleted) => !firstNavigationCompleted;

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsAllowed(e.Uri, _allowedHosts, _hostsAllowedByUser))
            return;

        e.Cancel = true;
        if (!IsWorthExplaining(e.Uri))
            return;

        // A cancelled navigation leaves the page exactly as it was, with no error of its own - which
        // is indistinguishable from a login that simply never finishes. Naming the host that was
        // turned away makes the block visible, and the notice offers to load it after all. The same
        // host goes into the log, so a report does not depend on the user having written it down
        // before closing the window.
        LogBlocked(e.Uri);
        ShowBlockedNotice(e.Uri);
    }

    /// <summary>The provider's own allow-list, plus whichever hosts the user has waved through on
    /// this window's own blocked notice. The waved-through ones live in this window and nowhere else:
    /// the next sign-in starts from the provider's list again, so a host that was right once never
    /// becomes a permanent hole. The https-only rule holds for both lists - a host the user allowed
    /// still never carries the session's cookies over plain http.</summary>
    internal static bool IsAllowed(string uri, IReadOnlyList<string> providerHosts, IReadOnlyList<string> hostsAllowedByUser) =>
        SignInNavigationPolicy.IsAllowedUri(uri, providerHosts)
        || SignInNavigationPolicy.IsAllowedExactHost(uri, hostsAllowedByUser);

    private const int MaxTitleHostLength = 48;

    /// <summary>The host the title shows next to its text, empty when it shows none. Only the real
    /// top-level address counts: the host comes from the browser's own record of where the page is
    /// (never from the page's title or anything else it can write), and the page can never change
    /// what stands in front of it. On the provider's own host and its subdomains there is nothing to
    /// add; on any other host, whether the provider's list or the person allowed it, the title carries
    /// that host, since nothing else in the window says where the page actually is. A long name is cut
    /// at the front: the end of a host is the part that tells whose it is.</summary>
    internal static string TitleHostFor(string? source, string providerHost)
    {
        if (source is null || SignInNavigationPolicy.IsUsageOrigin(source, providerHost)
            || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
            return "";

        var host = uri.IdnHost;
        return host.Length > MaxTitleHostLength ? "\u2026" + host[^(MaxTitleHostLength - 1)..] : host;
    }

    /// <summary>The title text: the fixed title, and the host after a middle dot while there is one.</summary>
    internal static string JoinTitle(string baseTitle, string host) =>
        host.Length == 0 ? baseTitle : baseTitle + " \u00B7 " + host;

    internal static string TitleFor(string baseTitle, string? source, string providerHost) =>
        JoinTitle(baseTitle, TitleHostFor(source, providerHost));

    /// <summary>Host shown behind the title, set from the browser's own address (see <see
    /// cref="TitleHostFor"/>) and nothing else.</summary>
    public static readonly DependencyProperty TitleHostProperty =
        DependencyProperty.Register(nameof(TitleHost), typeof(string), typeof(SignInWindow), new PropertyMetadata(""));

    public string TitleHost
    {
        get => (string)GetValue(TitleHostProperty);
        private set => SetValue(TitleHostProperty, value);
    }

    private sealed class TitleJoinConverter : System.Windows.Data.IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            JoinTitle(values[0] as string ?? "", values[1] as string ?? "");

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Title bar and window title stay live bindings on the translated title (so a language
    /// switch reaches them) and on <see cref="TitleHost"/>.</summary>
    private System.Windows.Data.MultiBinding NewTitleBinding() => new()
    {
        Converter = new TitleJoinConverter(),
        Bindings =
        {
            new System.Windows.Data.Binding("[SignIn.Title]") { Source = LocalizationService.Instance },
            new System.Windows.Data.Binding(nameof(TitleHost)) { Source = this },
        },
    };

    private void CoreWebView2_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e) =>
        TitleHost = TitleHostFor(Browser.CoreWebView2?.Source, _providerHost);

    /// <summary>Whether a turned-away navigation is worth a notice of its own. A page the user was
    /// trying to reach is; the blank page a redirected pop-up loads before its real address, and a
    /// handover to another program (a <c>mailto:</c> link, a provider's own desktop deep link), are
    /// not - those are cancelled quietly, because a notice there covers the login form over nothing
    /// the user did. Pure so the rule is testable without a WebView2 instance.</summary>
    internal static bool IsWorthExplaining(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp);

    /// <summary>Whether the notice may offer to load a turned-away address after all: only https
    /// ever passes <see cref="IsAllowed"/>, so for anything else the offer would loop.</summary>
    internal static bool CanAllow(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps;

    /// <summary>The blocked host, in the app's own log. Its own LogService instance rather than a
    /// constructor parameter threaded through every caller of this window: one line, written at most
    /// once per turned-away page, on a path that is already an unusual event.</summary>
    private static void LogBlocked(string uri) => LogService.Shared.LogInfo(
        $"Sign-in navigation blocked, not on this provider's allow list: {BlockedDetail(uri)}");

    /// <summary>Scheme, host and path of a turned-away address, never the query: a login carries its
    /// one-time codes there. Id-like path parts (an organisation id, a one-time code) are masked, as
    /// in the usage log. A turned-away address is often turned away for its scheme rather than its
    /// host, which is invisible in the host alone.</summary>
    internal static string BlockedDetail(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        ? $"{parsed.Scheme}://{parsed.Host}{AiUsage.Providers.WebUsageSource.MaskIds(parsed.AbsolutePath)}"
        : uri;

    /// <summary>The user's own decision on the host the notice has just named: allow it for as long
    /// as this window is open and go where the sign-in was heading. Written to the log as well - an
    /// allow-list being stepped past is exactly the kind of event that belongs in one.</summary>
    private void AllowBlockedHost_Click(object sender, RoutedEventArgs e)
    {
        if (_blockedUri is not { } uri || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return;

        _hostsAllowedByUser.Add(parsed.IdnHost);
        LogService.Shared.LogInfo(
            $"Sign-in navigation allowed by the user for this window: {parsed.IdnHost}");
        BlockedPanel.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;
        CursorRestore.Reset();
        Browser.CoreWebView2?.Navigate(uri);
    }

    private void ShowBlockedNotice(string uri)
    {
        _blockedUri = uri;
        // A url with no host of its own (about:blank from a redirected popup, mailto:, data:) still
        // parses, and its empty host would leave a sentence with a hole in it - the whole url is the
        // only honest thing left to name there.
        var host = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && !string.IsNullOrEmpty(parsed.IdnHost)
            ? parsed.IdnHost
            : uri;
        var loc = LocalizationService.Instance;
        BlockedHeadText.Text = loc["SignIn.Blocked.Head"];
        // "Allow" can never succeed for plain http (IsAllowed demands https for both lists), so
        // offering it would only bring the same notice back.
        var canAllow = CanAllow(uri);
        BlockedReasonText.Text = loc.Format("SignIn.Blocked.Reason", host)
            + (canAllow ? "" : " " + loc["SignIn.Blocked.PlainHttp"]);
        AllowBlockedButton.Visibility = canAllow ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        // The browser is a native child window, and WPF content is never composited over one (see the
        // comment in this window's XAML) - a notice drawn beside it would simply not be on screen
        // while a page is loaded, which is exactly when this one has something to say. Hiding the
        // page for as long as the notice stands is what makes it visible, the same way the
        // runtime-missing panel does it.
        Browser.Visibility = Visibility.Collapsed;
        BlockedPanel.Visibility = Visibility.Visible;
        // The page may have been mid-request for a cursor of its own when it went away; the notice
        // and every other window of the app need a visible pointer from this moment on.
        CursorRestore.Reset();
    }

    /// <summary>Dismisses the blocked-navigation notice and brings the page back: the blocked
    /// navigation never moved it, so the user lands exactly where the sign-in stopped and carries on,
    /// or closes the window. Skips the page while the runtime-missing panel owns the window, which
    /// has no page to bring back.</summary>
    private void DismissBlockedNotice_Click(object sender, RoutedEventArgs e)
    {
        BlockedPanel.Visibility = Visibility.Collapsed;
        if (RuntimeMissingPanel.Visibility != Visibility.Visible)
            Browser.Visibility = Visibility.Visible;
        CursorRestore.Reset();
    }

    /// <summary>True while the flow is on a login or identity-provider page rather than on the
    /// provider's own site: any host other than the provider's own is an identity provider it sent
    /// the user to, and on its own site the login paths are the ones that count. Kept pure so the
    /// rule is testable without a WebView2 instance.</summary>
    internal static bool IsOnLoginOrOAuthPage(Uri uri, string providerHost)
    {
        if (!SignInNavigationPolicy.IsUsageOrigin(uri.GetLeftPart(UriPartial.Path), providerHost))
            return true;

        var path = uri.AbsolutePath;
        return path.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/log-in", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/auth", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sign-in has nothing left to click through once the browser leaves the login/OAuth
    /// path and lands back on the real app - closing automatically here is what makes the window
    /// feel like it finished, instead of leaving the user staring at the chat page wondering whether
    /// anything happened. <see cref="Window.Closed"/> already triggers the caller's refresh. When that
    /// return happened almost instantly and never passed through a foreign host, it was never a login
    /// at all - the session was already signed in, and <see cref="ShowAlreadySignedIn"/> says so
    /// instead of closing the window before the user has seen anything.</summary>
    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_firstNavigationCompleted)
        {
            _firstNavigationCompleted = true;
            LoadingPanel.Visibility = ShouldShowLoadingIndicator(_firstNavigationCompleted)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (Browser.CoreWebView2?.Source is not { } source)
            return;

        var uri = new Uri(source);
        if (uri.Host != _providerHost)
            _sawForeignHost = true;

        if (IsOnLoginOrOAuthPage(uri, _providerHost))
        {
            _leftLoginPage = true;
            return;
        }

        // Only closes once the flow has actually been ON the login/OAuth path at least once -
        // otherwise the very first navigation to the provider's own start page (before the user has
        // done anything) would close the window instantly.
        if (_leftLoginPage && e.IsSuccess)
        {
            SignedIn = true;
            if (IsAlreadySignedIn(_leftLoginPage, _sawForeignHost, DateTimeOffset.UtcNow - _openedAt))
                ShowAlreadySignedIn();
            else
                Close();
        }
    }

    /// <summary>True when the flow's own return off the login/OAuth path was never a login at all: it
    /// bounced back almost instantly (within two seconds of the window opening) without ever passing
    /// through a host other than the provider's own - the same shape a provider's login page takes
    /// when the session sitting in this window's shared browser profile is already signed in, and
    /// redirects straight back without showing anything. A real sign-in either takes longer than that
    /// (typing credentials, a 2FA step) or passes through an identity provider's own host on the way,
    /// so <paramref name="sawForeignHost"/> alone already rules it out. Pure so the rule is testable
    /// without a WebView2 instance (same pattern as <see cref="IsOnLoginOrOAuthPage"/>).</summary>
    internal static bool IsAlreadySignedIn(bool leftLoginPage, bool sawForeignHost, TimeSpan sinceOpened) =>
        leftLoginPage && !sawForeignHost && sinceOpened < TimeSpan.FromSeconds(2);

    /// <summary>Takes the window the same way <see cref="ShowBlockedNotice"/> does, and for the same
    /// reason: the page is a native child window WPF content is never drawn over, so the code-behind
    /// hides it while this notice stands. The one button closes the window - the tile already treats
    /// <see cref="SignedIn"/> (set before this is called) as done, and starts reading through the
    /// session from its next tick.</summary>
    private void ShowAlreadySignedIn()
    {
        var loc = LocalizationService.Instance;
        AlreadySignedInHeadText.Text = loc["SignIn.AlreadySignedIn.Head"];
        AlreadySignedInReasonText.Text = loc["SignIn.AlreadySignedIn.Reason"];
        LoadingPanel.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Collapsed;
        AlreadySignedInPanel.Visibility = Visibility.Visible;
    }

    private void CloseAlreadySignedIn_Click(object sender, RoutedEventArgs e) => Close();
}
