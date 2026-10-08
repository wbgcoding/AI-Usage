using System.Net;
using System.Net.Http;
using AiUsage.Providers.LocalLogin;

namespace AiUsage.Tests;

public class LocalLoginHttpTests
{
    // A loopback listener on a free port; the handler runs once per request until disposed.
    private sealed class Loopback : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public string Origin { get; }
        public List<(string Path, string Body)> Requests { get; } = [];

        public Loopback(Func<HttpListenerRequest, string, HttpListenerResponse, Task> handle)
        {
            var port = FreePort();
            Origin = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Origin + "/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch (Exception) { return; }
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    lock (Requests)
                        Requests.Add((ctx.Request.Url!.AbsolutePath, body));
                    await handle(ctx.Request, body, ctx.Response);
                    ctx.Response.Close();
                }
            });
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            _listener.Close();
            try { _loop.Wait(2000); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task A_307_redirect_is_not_followed_and_the_body_never_reaches_the_second_url()
    {
        using var server = new Loopback((req, _, res) =>
        {
            if (req.Url!.AbsolutePath == "/first")
            {
                res.StatusCode = 307;
                res.Headers["Location"] = "/second";
            }
            else
            {
                res.StatusCode = 200;
            }
            return Task.CompletedTask;
        });

        using var client = LocalLoginHttp.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Origin + "/first")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("refresh_token", "secret-value")]),
        };

        var result = await LocalLoginHttp.ExecuteAsync(client, request, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(307, result.StatusCode);
        Assert.Equal("", result.Body);
        lock (server.Requests)
        {
            Assert.Single(server.Requests);
            Assert.DoesNotContain(server.Requests, r => r.Path == "/second");
        }
    }

    [Fact]
    public async Task A_two_megabyte_body_is_rejected()
    {
        using var server = new Loopback(async (_, _, res) =>
        {
            res.StatusCode = 200;
            var bytes = new byte[2 * 1024 * 1024];
            Array.Fill(bytes, (byte)'a');
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
        });

        using var client = LocalLoginHttp.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Origin + "/big");

        var result = await LocalLoginHttp.ExecuteAsync(client, request, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("", result.Body);
    }

    [Fact]
    public async Task A_small_body_is_returned()
    {
        using var server = new Loopback(async (_, _, res) =>
        {
            res.StatusCode = 200;
            await res.OutputStream.WriteAsync("{\"ok\":true}"u8.ToArray());
        });

        using var client = LocalLoginHttp.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Origin + "/small");

        var result = await LocalLoginHttp.ExecuteAsync(client, request, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("{\"ok\":true}", result.Body);
    }

    [Fact]
    public async Task A_chunked_body_over_the_cap_is_rejected()
    {
        using var server = new Loopback(async (_, _, res) =>
        {
            res.StatusCode = 200;
            res.SendChunked = true;
            var chunk = new byte[256 * 1024];
            Array.Fill(chunk, (byte)'a');
            for (var i = 0; i < 8; i++)
                await res.OutputStream.WriteAsync(chunk);
        });

        using var client = LocalLoginHttp.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Origin + "/chunked");

        var result = await LocalLoginHttp.ExecuteAsync(client, request, CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task A_body_that_stalls_after_the_headers_is_dropped_at_its_own_limit()
    {
        var release = new TaskCompletionSource();
        using var server = new Loopback(async (_, _, res) =>
        {
            res.StatusCode = 200;
            res.SendChunked = true;
            await res.OutputStream.WriteAsync("{\"a\":"u8.ToArray());
            await res.OutputStream.FlushAsync();
            await Task.WhenAny(release.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        });

        using var client = LocalLoginHttp.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Origin + "/stall");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var result = await LocalLoginHttp.ExecuteAsync(client, request, CancellationToken.None, TimeSpan.FromMilliseconds(300));
        release.SetResult();

        Assert.False(result.Ok);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "the stalled body must end at its own limit");
    }
}
