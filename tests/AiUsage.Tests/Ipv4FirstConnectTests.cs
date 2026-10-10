using System.Net;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class Ipv4FirstConnectTests
{
    [Fact]
    public void Order_puts_IPv4_before_IPv6_and_keeps_the_DNS_order_within_a_family()
    {
        var addresses = new[]
        {
            IPAddress.Parse("2607:6bc0::10"),
            IPAddress.Parse("160.79.104.10"),
            IPAddress.Parse("2001:4860:4846:400::"),
            IPAddress.Parse("172.217.119.4"),
        };

        var ordered = Ipv4FirstConnect.Order(addresses).Select(address => address.ToString()).ToArray();

        Assert.Equal(["160.79.104.10", "172.217.119.4", "2607:6bc0::10", "2001:4860:4846:400::"], ordered);
    }

    [Fact]
    public void Every_app_HTTP_handler_connects_through_the_IPv4_first_step()
    {
        var root = FindRepoRoot();
        foreach (var file in new[] { "src/AiUsage/Providers/LocalLogin/LocalLoginHttp.cs", "src/AiUsage/Services/UpdateCheck.cs", "src/AiUsage/Services/AllowListedDownloader.cs" })
            Assert.Contains("ConnectCallback = Ipv4FirstConnect.ConnectAsync", File.ReadAllText(Path.Combine(root, file)), StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build.bat")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
