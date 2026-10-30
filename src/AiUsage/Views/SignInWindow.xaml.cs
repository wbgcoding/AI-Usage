using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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
    private bool _sawLoginPage;
    // True after the user started a navigation (a link click or the form's own submit) on the
    // provider's own login page; cleared when the flow is back on a login page. A landing reached this
    // way keeps the window open only when it is a public information page.
    private bool _leftByLinkClick;
    private readonly string _startPath;
    private bool _firstNavigationCompleted;
    private bool _closed;
    // Both feed IsAlreadySignedIn below: the moment this window opened, and whether the flow ever
    // left the provider's own host on its way through the login/OAuth path.
    private DateTimeOffset _openedAt;
    private bool _sawForeignHost;
    private readonly string _accountKey;
    // The provider's own discovery script, asked on the landing page whether the session is real
    // (see SignInSessionCheck); null leaves the address alone to decide.
    private readonly string? _sessionCheckScript;
    private bool _checkingSession;
    // A login page that bounced straight back on a session the site no longer accepts gets its
    // cookies cleared and the login page loaded again, once per window.
    private bool _clearedStaleSession;
    // Asks the site now and then while its login page stands: a sign-in that finishes without leaving
    // the login address (a code typed into the page) is still noticed.
    private readonly DispatcherTimer _loginPagePoll = new() { Interval = LoginPagePollInterval };

    /// <summary>True once this window's own session came back off the login path and landed on the
    /// provider's own site again - the same moment the window decides it has finished and closes
    /// itself. It is the one thing the app can know for certain about a sign-in it cannot read the
    /// session of, so the tile takes it as "signed in" until one of its own reads says otherwise.</summary>
    public bool SignedIn { get; private set; }

    public SignInWindow(WebSessionDescriptor descriptor)
        : this(descriptor, new WebViewHost(descriptor), ProviderRegistry.SessionCheckScriptFor(descriptor.ProviderId))
    {
    }

    internal SignInWindow(WebSessionDescriptor descriptor, WebViewHost host, string? sessionCheckScript = null)
    {
        _host = host;
        _accountKey = descriptor.ProviderId;
        _sessionCheckScript = sessionCheckScript;
        _signInUrl = descriptor.SignInUrl;
        _providerHost = new Uri(descriptor.BaseUrl).Host;
        _startPath = new Uri(descriptor.BaseUrl).AbsolutePath;
        _allowedHosts = descriptor.AllowedHosts;
        InitializeComponent();
        _loginPagePoll.Tick += LoginPagePoll_Tick;
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
    /// version of the confirm dialog <see cref="WebSignInFlow"/> already offers before the window ever
    /// opens, for the case where the runtime disappeared between that check and here.</summary>
    private void OpenDownloadPage_Click(object sender, RoutedEventArgs e)
    {
        ShellOpen.TryOpen(WebViewAvailability.DownloadPageUrl);
    }

    /// <summary>Without this, the CoreWebView2 keeps this window alive through its own event
    /// subscriptions, and the WebView2 control's environment/controller lives until the process
    /// dies instead of being released the moment the sign-in dialog closes.</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _loginPagePoll.Stop();
        if (!SignedIn && _sawLoginPage)
            LogService.Shared.LogInfo($"Sign-in window for '{_accountKey}' closed before the sign-in finished.");
        if (Browser.CoreWebView2 is { } coreWebView2)
        {
            coreWebView2.NavigationStarting -= CoreWebView2_NavigationStarting;
            coreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            coreWebView2.SourceChanged -= CoreWebView2_SourceChanged;
        }
        ClosePopup();
        Browser.Dispose();
        CursorRestore.Reset();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _openedAt = DateTimeOffset.UtcNow;
        try
        {
            _environment = await _host.EnsureEnvironmentAsync();
            await Browser.EnsureCoreWebView2Async(_environment);
            Harden(Browser.CoreWebView2);
            Browser.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            Browser.CoreWebView2.SourceChanged += CoreWebView2_SourceChanged;
            Browser.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            // The page that started the sign-in asked to close; the window decides that itself.
            Browser.CoreWebView2.WindowCloseRequested += (_, _) =>
                LogService.Shared.LogInfo($"Sign-in window for '{_accountKey}': the page asked to close, ignored.");
            Browser.CoreWebView2.Navigate(_signInUrl);
            if (_sessionCheckScript is not null)
                _loginPagePoll.Start();
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

    /// <summary>The settings every browser in this window gets, the page and any popup alike.</summary>
    private static void Harden(CoreWebView2 core)
    {
        // The user works in this window (typing a password, clicking a consent screen), so its
        // own right-click menu stays - only the developer tools go, the same as the hidden reader
        // session this window shares a profile with.
        core.Settings.AreDevToolsEnabled = false;
        // The email typed at sign-in must not land in the profile's autofill store, and a
        // permission click here (notifications, location) must not be saved where the hidden
        // reader session inherits it: every request gets a no that is not remembered.
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.PermissionRequested += (_, args) =>
        {
            args.State = CoreWebView2PermissionState.Deny;
            args.SavesInProfile = false;
        };
        // No provider sign-in needs a file: every download the page starts is cancelled.
        core.DownloadStarting += (_, args) => args.Cancel = true;
    }

    private CoreWebView2Environment? _environment;
    private Microsoft.Web.WebView2.Wpf.WebView2? _popup;

    /// <summary>"Sign in with Google" opens a popup and waits for its answer through the popup's link
    /// back to the page that opened it. The popup gets its own browser over the page, on the same
    /// profile and allow-list; loading it in place of the page would cut that link, and the sign-in
    /// would never finish. One popup at a time: a further one loads in the open popup.</summary>
    private async void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        if (_closed)
            return;

        if (!IsPopupAddressAllowed(args.Uri))
        {
            args.Handled = true;
            if (IsWorthExplaining(args.Uri))
            {
                LogBlocked(args.Uri);
                ShowBlockedNotice(args.Uri);
            }
            return;
        }

        if (_popup?.CoreWebView2 is { } open)
        {
            args.Handled = true;
            open.Navigate(args.Uri);
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            var popup = new Microsoft.Web.WebView2.Wpf.WebView2();
            _popup = popup;
            PopupHost.Children.Add(popup);
            PopupHost.Visibility = Visibility.Visible;
            // Both browsers are native child windows, and the first one created stays on top: the
            // page steps aside while the popup stands (it keeps running and gets the answer).
            Browser.Visibility = Visibility.Hidden;
            await popup.EnsureCoreWebView2Async(_environment);
            if (_closed || _popup != popup)
            {
                args.Handled = true;
                return;
            }

            Harden(popup.CoreWebView2);
            popup.CoreWebView2.NavigationStarting += Popup_NavigationStarting;
            popup.CoreWebView2.NewWindowRequested += (_, inner) =>
            {
                // A popup of the popup loads in the popup itself.
                inner.Handled = true;
                if (IsPopupAddressAllowed(inner.Uri))
                    popup.CoreWebView2.Navigate(inner.Uri);
            };
            popup.CoreWebView2.WindowCloseRequested += (_, _) => ClosePopup();
            // The title names the host the popup stands on, the same way it does for the page.
            popup.CoreWebView2.SourceChanged += (_, _) =>
            {
                if (_popup == popup)
                    TitleHost = TitleHostFor(popup.CoreWebView2.Source, _providerHost);
            };
            args.NewWindow = popup.CoreWebView2;
            args.Handled = true;
            LogService.Shared.LogInfo($"Sign-in window for '{_accountKey}': the page opened a popup.");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // No popup browser: the address loads in the page itself, as a last resort.
            LogService.Shared.LogInfo($"Sign-in window for '{_accountKey}': the popup failed ({ex.GetType().Name}), loading it in the page.");
            ClosePopup();
            args.Handled = true;
            Browser.CoreWebView2?.Navigate(args.Uri);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>A popup starts blank more often than not and is then sent on by script; the address
    /// it finally loads still has to pass the allow-list.</summary>
    private bool IsPopupAddressAllowed(string uri) =>
        uri is "" or "about:blank" || IsAllowed(uri, _allowedHosts, _hostsAllowedByUser);

    private void Popup_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsPopupAddressAllowed(e.Uri))
            return;

        e.Cancel = true;
        if (!IsWorthExplaining(e.Uri))
            return;
        LogBlocked(e.Uri);
        ClosePopup();
        ShowBlockedNotice(e.Uri);
    }

    /// <summary>The popup closed itself (its answer went to the page) or is given up: the page shows again.</summary>
    private void ClosePopup()
    {
        if (_popup is not { } popup)
            return;

        _popup = null;
        PopupHost.Children.Remove(popup);
        PopupHost.Visibility = Visibility.Collapsed;
        // A notice standing in the page's place keeps the page hidden.
        if (BlockedPanel.Visibility != Visibility.Visible && AlreadySignedInPanel.Visibility != Visibility.Visible)
            Browser.Visibility = Visibility.Visible;
        popup.Dispose();
        if (_popup is null)
            TitleHost = TitleHostFor(Browser.CoreWebView2?.Source, _providerHost);
        if (!_closed)
            LogService.Shared.LogInfo($"Sign-in window for '{_accountKey}': the popup closed.");
    }

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsAllowed(e.Uri, _allowedHosts, _hostsAllowedByUser))
        {
            if (e.IsUserInitiated && Browser.CoreWebView2?.Source is { } current
                && Uri.TryCreate(current, UriKind.Absolute, out var from)
                && IsOnProviderLoginPage(from, _providerHost))
                _leftByLinkClick = true;
            return;
        }

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

    /// <summary>Keeps the title on the current host, and judges an in-page route change (no new
    /// document, so no NavigationCompleted follows) the same way as a finished navigation: single-page
    /// sites such as claude.ai move from their login form to the app by script alone.</summary>
    private void CoreWebView2_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (_popup is null)
            TitleHost = TitleHostFor(Browser.CoreWebView2?.Source, _providerHost);
        if (!e.IsNewDocument)
            EvaluateLanding(isSuccess: true);
    }

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

    /// <summary>True on a login path of the provider's own site (not on an identity provider's host).</summary>
    internal static bool IsOnProviderLoginPage(Uri uri, string providerHost) =>
        SignInNavigationPolicy.IsUsageOrigin(uri.GetLeftPart(UriPartial.Path), providerHost)
        && IsOnLoginOrOAuthPage(uri, providerHost);

    /// <summary>True when the landing page is the provider's start page: the site root, the page the
    /// descriptor's address names, or one of its parent paths.</summary>
    internal static bool IsAppStartPage(Uri landing, string startPath)
    {
        var path = landing.AbsolutePath.TrimEnd('/');
        var start = startPath.TrimEnd('/');
        return path.Length == 0
            || string.Equals(path, start, StringComparison.OrdinalIgnoreCase)
            || start.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] PublicInfoSegments =
    [
        "legal", "terms", "privacy", "policies", "policy", "pricing", "plans", "docs", "help", "support", "blog",
        "news", "about", "careers", "jobs", "download", "downloads", "security", "enterprise", "features",
        "contact", "cookies", "status",
    ];

    /// <summary>True when the landing is a public information page of the provider's site (terms,
    /// privacy, pricing, help and the like) judged by the first path segment: a page a visitor can
    /// read without an account, so reaching it says nothing about a finished sign-in.</summary>
    internal static bool IsPublicInfoPage(Uri landing)
    {
        var first = landing.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && PublicInfoSegments.Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether a landing off the login path ends the sign-in. After a navigation the user
    /// started (a link click, but also the login form's own submit) the window stays open only when
    /// the landing is a public information page that is not the app's start page: the user read the
    /// terms, nothing finished. Every other landing counts as signed in, as does any way off the
    /// login path that was not user-initiated (a redirect chain after the credentials).</summary>
    internal static bool ShouldCloseAfterSignIn(Uri landing, string startPath, bool leftByLinkClick) =>
        !leftByLinkClick || IsAppStartPage(landing, startPath) || !IsPublicInfoPage(landing);

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

        EvaluateLanding(e.IsSuccess);
    }

    private void EvaluateLanding(bool isSuccess)
    {
        if (_closed || Browser.CoreWebView2?.Source is not { } source
            || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
            return;

        if (uri.Host != _providerHost)
            _sawForeignHost = true;

        if (IsOnLoginOrOAuthPage(uri, _providerHost))
        {
            _sawLoginPage = true;
            _leftByLinkClick = false;
            return;
        }

        // Only closes once the flow has actually been ON the login/OAuth path at least once -
        // otherwise the very first navigation to the provider's own start page (before the user has
        // done anything) would close the window instantly.
        if (_sawLoginPage && isSuccess && !SignedIn && !_checkingSession && ShouldCloseAfterSignIn(uri, _startPath, _leftByLinkClick))
            _ = ConfirmSignInAsync();
    }

    /// <summary>How often and how far apart the landing page is asked: a session cookie can land a
    /// moment after the page that set it, so one "not signed in" right away is not the last word. A
    /// quick bounce is a stored session the site either takes or not, so it gets fewer tries than a
    /// real sign-in that may still be settling.</summary>
    internal static int SessionCheckAttempts(bool quickBounce) => quickBounce ? 2 : 8;
    internal static readonly TimeSpan SessionCheckPause = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SessionCheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The landing looks like a finished sign-in; the site itself confirms it before the
    /// window says so. A confirmed or unclear session closes the window as before. A session the site
    /// rejects keeps it open: on a quick bounce (a stale session the login page waved through) the
    /// window clears this account's cookies and shows the login page again, otherwise the user is
    /// still mid-flow on a page that only looks finished.</summary>
    private async Task ConfirmSignInAsync()
    {
        _checkingSession = true;
        try
        {
            var quickBounce = IsAlreadySignedIn(_sawLoginPage, _sawForeignHost, DateTimeOffset.UtcNow - _openedAt);
            var state = await CheckSessionAsync(SessionCheckAttempts(quickBounce));
            if (_closed || SignedIn || Browser.CoreWebView2?.Source is not { } source
                || !Uri.TryCreate(source, UriKind.Absolute, out var now) || IsOnLoginOrOAuthPage(now, _providerHost))
                return;

            switch (NextStep(state, quickBounce, _clearedStaleSession))
            {
                case SignInLandingStep.RetryWithoutSession:
                    _clearedStaleSession = true;
                    LogService.Shared.LogInfo($"Sign-in window for '{_accountKey}': the site no longer accepts the stored session, loading the login page again.");
                    Browser.CoreWebView2.CookieManager.DeleteAllCookies();
                    _sawLoginPage = false;
                    _sawForeignHost = false;
                    _openedAt = DateTimeOffset.UtcNow;
                    Browser.CoreWebView2.Navigate(_signInUrl);
                    return;
                case SignInLandingStep.StayOpen:
                    LogService.Shared.LogInfo($"Sign-in window for '{_accountKey}': the page left the login, but the site reports no session yet; the window stays open.");
                    return;
            }

            SignedIn = true;
            LogService.Shared.LogInfo(state == SignInSessionState.SignedIn
                ? $"Sign-in window for '{_accountKey}': signed in, session confirmed."
                : $"Sign-in window for '{_accountKey}': signed in, the site gave no clear answer on the session.");
            if (quickBounce)
                ShowAlreadySignedIn();
            else
                Close();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // The browser went away while the check ran: the window is closing anyway.
        }
        finally
        {
            _checkingSession = false;
        }
    }

    private async Task<SignInSessionState> CheckSessionAsync(int attempts)
    {
        if (_sessionCheckScript is null)
            return SignInSessionState.Unknown;

        var state = SignInSessionState.Unknown;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            // Closed, or the user went back to the login page: the next landing asks again.
            if (_closed || Browser.CoreWebView2 is not { } core
                || !Uri.TryCreate(core.Source, UriKind.Absolute, out var page) || IsOnLoginOrOAuthPage(page, _providerHost))
                return state;
            state = await SignInSessionCheck.RunAsync(core, _sessionCheckScript, _providerHost, SessionCheckTimeout);
            if (state != SignInSessionState.NotSignedIn)
                return state;
            if (attempt < attempts)
                await Task.Delay(SessionCheckPause);
        }
        return state;
    }

    internal static readonly TimeSpan LoginPagePollInterval = TimeSpan.FromSeconds(4);

    /// <summary>Whether the timer asks the site on this page: only on the provider's own login page, and
    /// not in the first seconds, where a stored session bounces through on its own. Pure so the rule is
    /// testable without a WebView2 instance.</summary>
    internal static bool ShouldPollLoginPage(Uri page, string providerHost, TimeSpan sinceOpened) =>
        sinceOpened >= TimeSpan.FromSeconds(2) && IsOnProviderLoginPage(page, providerHost);

    private async void LoginPagePoll_Tick(object? sender, EventArgs e)
    {
        if (_closed || SignedIn || _checkingSession || _sessionCheckScript is null
            || Browser.CoreWebView2 is not { } core || !Uri.TryCreate(core.Source, UriKind.Absolute, out var page)
            || !ShouldPollLoginPage(page, _providerHost, DateTimeOffset.UtcNow - _openedAt))
            return;

        _checkingSession = true;
        try
        {
            // Only a clear "ok" counts here: the page itself still says "log in".
            if (await SignInSessionCheck.RunAsync(core, _sessionCheckScript, _providerHost, SessionCheckTimeout) != SignInSessionState.SignedIn
                || _closed || SignedIn)
                return;

            SignedIn = true;
            LogService.Shared.LogInfo($"Sign-in window for '{_accountKey}': signed in while the login page stood, session confirmed.");
            Close();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // The browser went away while the check ran: the window is closing anyway.
        }
        finally
        {
            _checkingSession = false;
        }
    }

    /// <summary>What a landing does with the site's answer. Pure so the rule is testable without a
    /// WebView2 instance.</summary>
    internal static SignInLandingStep NextStep(SignInSessionState state, bool quickBounce, bool alreadyCleared) => state switch
    {
        SignInSessionState.NotSignedIn when quickBounce && !alreadyCleared => SignInLandingStep.RetryWithoutSession,
        SignInSessionState.NotSignedIn => SignInLandingStep.StayOpen,
        _ => SignInLandingStep.Finish,
    };

    /// <summary>True when the flow's own return off the login/OAuth path was never a login at all: it
    /// bounced back almost instantly (within two seconds of the window opening) without ever passing
    /// through a host other than the provider's own - the same shape a provider's login page takes
    /// when the session sitting in this window's shared browser profile is already signed in, and
    /// redirects straight back without showing anything. A real sign-in either takes longer than that
    /// (typing credentials, a 2FA step) or passes through an identity provider's own host on the way,
    /// so <paramref name="sawForeignHost"/> alone already rules it out. Pure so the rule is testable
    /// without a WebView2 instance (same pattern as <see cref="IsOnLoginOrOAuthPage"/>).</summary>
    internal static bool IsAlreadySignedIn(bool sawLoginPage, bool sawForeignHost, TimeSpan sinceOpened) =>
        sawLoginPage && !sawForeignHost && sinceOpened < TimeSpan.FromSeconds(2);

    /// <summary>Takes the window the same way <see cref="ShowBlockedNotice"/> does, and for the same
    /// reason: the page is a native child window WPF content is never drawn over, so the code-behind
    /// hides it while this notice stands. The one button closes the window - the tile already treats
    /// <see cref="SignedIn"/> (set by the caller) as done, and starts reading through the
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

/// <summary>What the sign-in window does after asking the site about a landing.</summary>
internal enum SignInLandingStep
{
    Finish,
    StayOpen,
    RetryWithoutSession,
}
