using AiUsage.Web;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// The real hidden host cannot start a browser in a test process, but its disposal race is reachable
/// through the environment factory: a dispose that lands while the environment is still being created
/// must stop the navigation before any window or browser is built.
/// </summary>
public class WebView2HiddenBrowserHostTests
{
    private static readonly WebSessionDescriptor Descriptor = new(
        "test", "https://example.test/usage", "https://example.test/login", ["example.test"], "test-profile");

    private static (WebView2HiddenBrowserHost Host, TaskCompletionSource<CoreWebView2Environment> Environment) Build()
    {
        var environment = new TaskCompletionSource<CoreWebView2Environment>();
        var webViewHost = new WebViewHost(TestPaths.GetPath("hidden-host-profile"), _ => environment.Task);
        return (new WebView2HiddenBrowserHost(webViewHost, Descriptor), environment);
    }

    [Fact]
    public async Task A_dispose_while_the_environment_is_created_builds_no_browser()
    {
        var (host, environment) = Build();

        var navigation = host.NavigateAsync(CancellationToken.None);
        await host.DisposeAsync();
        environment.SetResult(null!);

        // Building the window would need a UI thread this test thread does not have and would throw.
        Assert.False(await navigation);
    }

    [Fact]
    public async Task A_navigation_started_after_dispose_answers_false_at_once()
    {
        var (host, _) = Build();
        await host.DisposeAsync();

        Assert.False(await host.NavigateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_cancel_while_the_environment_is_created_ends_the_navigation()
    {
        var (host, _) = Build();
        using var cancel = new CancellationTokenSource();

        var navigation = host.NavigateAsync(cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
        await host.DisposeAsync();
    }
}
