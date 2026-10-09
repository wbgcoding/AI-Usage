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

    private const string OkToken = """{"access_token":"fresh-token","expires_in":3599}""";

    [Fact]
    public async Task When_no_pair_is_accepted_and_none_failed_in_transit_the_cached_pairs_are_dropped()
    {
        var forgotten = 0;

        await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-forget-all-client", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: (_, _, _) => Answer(401, """{"error":"invalid_client"}"""),
            discoverPairs: () => TwoPairs, forgetPairs: () => forgotten++);

        Assert.Equal(1, forgotten);
    }

    [Fact]
    public async Task A_transport_failure_or_an_accepted_pair_keeps_the_cached_pairs()
    {
        var forgotten = 0;

        await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-keep-transport", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: (_, _, _) => Task.FromResult(new LocalLoginHttp.Response(false, 0, "")),
            discoverPairs: () => TwoPairs, forgetPairs: () => forgotten++);
        await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-keep-grant", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: (_, _, _) => Answer(400, """{"error":"invalid_grant"}"""),
            discoverPairs: () => TwoPairs, forgetPairs: () => forgotten++);
        await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-keep-ok", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: (_, _, _) => Answer(200, OkToken),
            discoverPairs: () => TwoPairs, forgetPairs: () => forgotten++);

        Assert.Equal(0, forgotten);
    }

    [Fact]
    public async Task The_accepted_pair_is_tried_first_on_the_next_refresh()
    {
        var tried = new List<string>();
        Task<LocalLoginHttp.Response> Post(string url, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
        {
            var id = form.Single(pair => pair.Key == "client_id").Value;
            tried.Add(id);
            // Only the second pair is a real client; its token lives for 10 s, so the next call refreshes again.
            return id == "id-2"
                ? Answer(200, """{"access_token":"fresh-token","expires_in":10}""")
                : Answer(401, """{"error":"invalid_client"}""");
        }

        var first = await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-accepted-first", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: Post, discoverPairs: () => TwoPairs, forgetPairs: () => { });
        var second = await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-accepted-first", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: Post, discoverPairs: () => TwoPairs, forgetPairs: () => { });

        Assert.Equal("fresh-token", first.Token);
        Assert.Equal("fresh-token", second.Token);
        Assert.Equal(["id-1", "id-2", "id-2"], tried);
    }

    [Fact]
    public async Task An_expires_in_written_as_text_keeps_the_valid_token_and_caches_it_for_55_minutes()
    {
        var posts = 0;
        Task<LocalLoginHttp.Response> Post(string url, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
        {
            posts++;
            return Answer(200, """{"access_token":"fresh-token","expires_in":"3599"}""");
        }

        var first = await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-text-lifetime", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: Post, discoverPairs: () => TwoPairs, forgetPairs: () => { });
        var second = await AntigravityLocalLogin.ResolveAccessTokenAsync(
            "refresh-text-lifetime", null, DateTimeOffset.MinValue, CancellationToken.None,
            post: Post, discoverPairs: () => TwoPairs, forgetPairs: () => { });

        Assert.Equal("fresh-token", first.Token);
        Assert.Equal("fresh-token", second.Token);
        Assert.Equal(1, posts);
    }

    private string NewExe(byte[] content)
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-agy-scan");
        _directories.Add(directory);
        var exe = Path.Combine(directory, "agy.exe");
        File.WriteAllBytes(exe, content);
        return exe;
    }

    [Fact]
    public void An_exe_without_a_pair_is_scanned_once_until_its_size_or_write_time_changes()
    {
        AntigravityOAuthClient.ForgetPairs();
        var exe = NewExe([1, 2, 3]);
        var scans = 0;
        (List<(string, string)>, bool) Scan(string path)
        {
            scans++;
            return ([], true);
        }

        AntigravityOAuthClient.DiscoverPairs(exe, Scan);
        AntigravityOAuthClient.DiscoverPairs(exe, Scan);
        Assert.Equal(1, scans);

        // An update changes the file: scanned again.
        File.WriteAllBytes(exe, [1, 2, 3, 4]);
        AntigravityOAuthClient.DiscoverPairs(exe, Scan);
        Assert.Equal(2, scans);

        File.SetLastWriteTimeUtc(exe, File.GetLastWriteTimeUtc(exe).AddMinutes(5));
        AntigravityOAuthClient.DiscoverPairs(exe, Scan);
        Assert.Equal(3, scans);

        // Forgetting the cache also forgets the memo.
        AntigravityOAuthClient.ForgetPairs();
        AntigravityOAuthClient.DiscoverPairs(exe, Scan);
        Assert.Equal(4, scans);
        AntigravityOAuthClient.ForgetPairs();
    }

    [Fact]
    public void A_scan_that_could_not_read_the_file_is_not_remembered()
    {
        AntigravityOAuthClient.ForgetPairs();
        var exe = NewExe([1, 2, 3]);
        var scans = 0;
        (List<(string, string)>, bool) Scan(string path)
        {
            scans++;
            return ([], false);
        }

        AntigravityOAuthClient.DiscoverPairs(exe, Scan);
        AntigravityOAuthClient.DiscoverPairs(exe, Scan);

        Assert.Equal(2, scans);
        AntigravityOAuthClient.ForgetPairs();
    }

    [Fact]
    public void Found_pairs_are_cached_until_they_are_forgotten()
    {
        AntigravityOAuthClient.ForgetPairs();
        var exe = NewExe([1, 2, 3]);
        var scans = 0;
        (List<(string ClientId, string ClientSecret)>, bool) Scan(string path)
        {
            scans++;
            return ([("id-1", "secret-1")], true);
        }

        Assert.Single(AntigravityOAuthClient.DiscoverPairs(exe, Scan));
        Assert.Single(AntigravityOAuthClient.DiscoverPairs(exe, Scan));
        Assert.Equal(1, scans);

        AntigravityOAuthClient.ForgetPairs();
        AntigravityOAuthClient.DiscoverPairs(exe, Scan);
        Assert.Equal(2, scans);
        AntigravityOAuthClient.ForgetPairs();
    }

    private readonly List<DisposableTestDirectory> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories)
            directory.Dispose();
    }
}
