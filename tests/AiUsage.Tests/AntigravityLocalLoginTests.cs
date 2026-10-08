using System.Text;
using AiUsage.Providers.LocalLogin;

namespace AiUsage.Tests;

public class AntigravityLocalLoginTests : IDisposable
{
    private static readonly IReadOnlyList<(string ClientId, string ClientSecret)> TwoPairs =
        [("id-1", "secret-1"), ("id-2", "secret-2")];

    private static Task<LocalLoginHttp.Response> Answer(int status, string body) =>
        Task.FromResult(new LocalLoginHttp.Response(status is >= 200 and < 300, status, body));

    [Fact]
    public async Task When_every_pair_answers_invalid_grant_the_refresh_reports_a_signed_out_token()
    {
        var attempts = 0;
        var (token, rejected) = await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-all-invalid", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: (_, _, _) => { attempts++; return Answer(400, """{"error":"invalid_grant"}"""); },
            discoverPairs: () => TwoPairs);

        Assert.Null(token);
        Assert.True(rejected);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_401_invalid_grant_counts_the_same_as_a_400()
    {
        var (_, rejected) = await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-401", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: (_, _, _) => Answer(401, """{"error":"invalid_grant"}"""),
            discoverPairs: () => TwoPairs);

        Assert.True(rejected);
    }

    [Fact]
    public async Task A_network_failure_among_the_pairs_is_not_a_sign_out()
    {
        var call = 0;
        var (token, rejected) = await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-mixed", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: (_, _, _) => ++call == 1
                ? Answer(400, """{"error":"invalid_grant"}""")
                : Task.FromResult(new LocalLoginHttp.Response(false, 0, "")),
            discoverPairs: () => TwoPairs);

        Assert.Null(token);
        Assert.False(rejected);
    }

    [Fact]
    public async Task One_invalid_grant_among_wrong_client_answers_is_a_sign_out()
    {
        var call = 0;
        var (token, rejected) = await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-client-then-grant", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: (_, _, _) => ++call == 1
                ? Answer(401, """{"error":"invalid_client"}""")
                : Answer(400, """{"error":"invalid_grant"}"""),
            discoverPairs: () => TwoPairs);

        Assert.Null(token);
        Assert.True(rejected);
    }

    [Fact]
    public async Task A_transport_failure_next_to_an_invalid_grant_stays_undecided()
    {
        var call = 0;
        var (_, rejected) = await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-three", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: (_, _, _) => ++call switch
            {
                1 => Answer(401, """{"error":"invalid_client"}"""),
                2 => Answer(400, """{"error":"invalid_grant"}"""),
                _ => Task.FromResult(new LocalLoginHttp.Response(false, 0, "")),
            },
            discoverPairs: () => [("id-1", "secret-1"), ("id-2", "secret-2"), ("id-3", "secret-3")]);

        Assert.False(rejected);
    }

    [Fact]
    public async Task A_wrong_client_error_is_not_a_sign_out()
    {
        var (_, rejected) = await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-client", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: (_, _, _) => Answer(401, """{"error":"invalid_client"}"""),
            discoverPairs: () => TwoPairs);

        Assert.False(rejected);
    }

    [Fact]
    public async Task No_discovered_pairs_is_a_failure_not_a_sign_out()
    {
        var (_, rejected) = await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-none", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: (_, _, _) => Answer(400, """{"error":"invalid_grant"}"""),
            discoverPairs: () => []);

        Assert.False(rejected);
    }

    [Fact]
    public void Ten_ids_and_ten_secrets_in_the_binary_yield_at_most_sixteen_pairs_in_discovery_order()
    {
        var text = new StringBuilder();
        for (var i = 0; i < 10; i++)
        {
            // The duplicate of every id and secret must not use up a slot.
            var id = $"{100000 + i}-{new string((char)('a' + i), 32)}.apps.googleusercontent.com";
            var secret = $"GOCSPX-{new string((char)('a' + i), 28)}";
            text.Append(id).Append(' ').Append(id).Append(' ').Append(secret).Append(' ').Append(secret).Append('\0');
        }

        var directory = TestPaths.CreateDisposableDirectory("ai-usage-agy");
        _directories.Add(directory);
        var exe = Path.Combine(directory, "agy.exe");
        File.WriteAllBytes(exe, Encoding.Latin1.GetBytes(text.ToString()));

        var pairs = AntigravityOAuthClient.ScanForPairs(exe);

        Assert.Equal(16, pairs.Count);
        Assert.Equal(4, pairs.Select(p => p.ClientId).Distinct().Count());
        Assert.Equal(4, pairs.Select(p => p.ClientSecret).Distinct().Count());
        Assert.StartsWith("100000-", pairs[0].ClientId);
        Assert.Equal("GOCSPX-" + new string('a', 28), pairs[0].ClientSecret);
    }

    private readonly List<DisposableTestDirectory> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories)
            directory.Dispose();
    }
}
