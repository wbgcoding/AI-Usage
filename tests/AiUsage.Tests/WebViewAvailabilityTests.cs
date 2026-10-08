using AiUsage.Web;

namespace AiUsage.Tests;

public class WebViewAvailabilityTests
{
    [Fact]
    public void IsInstalled_returns_true_when_the_probe_reports_a_version()
    {
        Assert.True(WebViewAvailability.IsInstalled(() => "128.0.0.0"));
    }

    [Fact]
    public void IsInstalled_returns_false_when_the_probe_returns_nothing()
    {
        Assert.False(WebViewAvailability.IsInstalled(() => null));
    }

    [Fact]
    public void IsInstalled_returns_false_when_the_probe_reports_the_runtime_missing()
    {
        Assert.False(WebViewAvailability.IsInstalled(() =>
            throw new Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException("missing")));
    }
}
