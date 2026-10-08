using AiUsage.Web;

namespace AiUsage.Tests;

public class SignInNavigationPolicyTests
{
    private static readonly string[] AllowedHosts = ["claude.ai", "anthropic.com", "accounts.google.com"];

    [Theory]
    [InlineData("claude.ai")]
    [InlineData("www.claude.ai")]
    [InlineData("anthropic.com")]
    [InlineData("login.anthropic.com")]
    [InlineData("accounts.google.com")]
    public void Allows_claude_anthropic_and_google_sign_in_hosts_and_their_subdomains(string host)
    {
        Assert.True(SignInNavigationPolicy.IsAllowedHost(host, AllowedHosts));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("claude.ai.example.com")]
    [InlineData("google.com")]
    [InlineData("accounts.google.com.example.com")]
    public void Rejects_unrelated_hosts_and_look_alikes(string host)
    {
        Assert.False(SignInNavigationPolicy.IsAllowedHost(host, AllowedHosts));
    }

    private static readonly string[] CountryAccountHosts = ["google.com", "youtube.com", SignInNavigationPolicy.GoogleCountryAccounts];

    [Theory]
    [InlineData("accounts.google.de")]
    [InlineData("accounts.google.co.uk")]
    [InlineData("accounts.google.com.br")]
    [InlineData("ACCOUNTS.GOOGLE.FR")]
    [InlineData("accounts.google.com")]
    [InlineData("accounts.youtube.com")]
    public void Allows_googles_country_account_hosts_when_the_pattern_is_listed(string host)
    {
        Assert.True(SignInNavigationPolicy.IsAllowedHost(host, CountryAccountHosts));
    }

    [Theory]
    [InlineData("accounts.google.de.evil.com")]
    [InlineData("evilaccounts.google.de")]
    [InlineData("accounts-google.de")]
    [InlineData("accounts.google.zz")]
    [InlineData("accounts.google.co.evil")]
    [InlineData("accounts.google.")]
    [InlineData("login.accounts.google.de")]
    [InlineData("google.de")]
    [InlineData("accounts.example.de")]
    public void Rejects_look_alikes_and_unknown_tlds_of_the_country_account_pattern(string host)
    {
        Assert.False(SignInNavigationPolicy.IsAllowedHost(host, CountryAccountHosts));
    }

    [Fact]
    public void The_country_account_pattern_does_nothing_unless_listed()
    {
        Assert.False(SignInNavigationPolicy.IsAllowedHost("accounts.google.de", AllowedHosts));
    }

    [Fact]
    public void The_country_account_pattern_still_needs_https()
    {
        Assert.True(SignInNavigationPolicy.IsAllowedUri("https://accounts.google.de/signin/v2", CountryAccountHosts));
        Assert.False(SignInNavigationPolicy.IsAllowedUri("http://accounts.google.de/signin/v2", CountryAccountHosts));
    }

    [Fact]
    public void Allows_an_https_navigation_to_an_allowed_host()
    {
        Assert.True(SignInNavigationPolicy.IsAllowedUri("https://claude.ai/login", AllowedHosts));
    }

    [Fact]
    public void Rejects_the_same_host_over_plain_http()
    {
        Assert.False(SignInNavigationPolicy.IsAllowedUri("http://claude.ai/login", AllowedHosts));
    }

    [Fact]
    public void Rejects_an_https_look_alike_host()
    {
        Assert.False(SignInNavigationPolicy.IsAllowedUri("https://claude.ai.example.com/", AllowedHosts));
    }

    [Fact]
    public void Rejects_a_relative_string_that_is_not_an_absolute_uri()
    {
        Assert.False(SignInNavigationPolicy.IsAllowedUri("/login", AllowedHosts));
    }

    [Theory]
    [InlineData("https://claude.ai/api/usage", true)]
    [InlineData("https://www.claude.ai/api/usage", true)]
    [InlineData("https://accounts.google.com/o/oauth2/auth", false)]
    [InlineData("https://claude.ai.example.com/api/usage", false)]
    [InlineData("http://claude.ai/api/usage", false)]
    public void IsUsageOrigin_only_allows_https_claude_ai_and_its_subdomains(string url, bool expected)
    {
        Assert.Equal(expected, SignInNavigationPolicy.IsUsageOrigin(url, "claude.ai"));
    }
}
