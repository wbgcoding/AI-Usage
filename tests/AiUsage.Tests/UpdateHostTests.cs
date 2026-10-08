using System.Net;
using System.Net.Http;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class UpdateHostTests
{
    private static readonly string[] Roots = [@"C:\Program Files", @"C:\Program Files (x86)"];

    [Fact]
    public void A_copy_under_Program_Files_updates_for_all_users()
    {
        Assert.Equal("/SILENT /ALLUSERS", UpdateHost.BuildSetupArguments(@"C:\Program Files\AI-Usage\AI-Usage.exe", Roots));
        Assert.Equal("/SILENT /ALLUSERS", UpdateHost.BuildSetupArguments(@"C:\Program Files (x86)\AI-Usage\AI-Usage.exe", Roots));
    }

    [Fact]
    public void A_per_user_copy_updates_for_the_current_user()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var exe = Path.Combine(local, "Programs", "AI-Usage", "AI-Usage.exe");

        Assert.Equal("/SILENT /CURRENTUSER", UpdateHost.BuildSetupArguments(exe));
        Assert.Equal("/SILENT /CURRENTUSER", UpdateHost.BuildSetupArguments(exe, Roots));
    }

    [Fact]
    public void A_folder_that_only_shares_the_Program_Files_prefix_is_per_user()
    {
        Assert.Equal("/SILENT /CURRENTUSER", UpdateHost.BuildSetupArguments(@"C:\Program Files Extra\AI-Usage\AI-Usage.exe", Roots));
        Assert.Equal("/SILENT /CURRENTUSER", UpdateHost.BuildSetupArguments(null, Roots));
    }

    [Fact]
    public void A_per_machine_install_in_a_custom_folder_updates_for_all_users()
    {
        var exe = @"D:\Tools\AI-Usage\AI-Usage.exe";

        Assert.Equal("/SILENT /ALLUSERS", UpdateHost.BuildSetupArguments(exe, Roots, perMachine => perMachine ? @"D:\Tools\AI-Usage\" : null));
    }

    [Fact]
    public void A_per_user_install_in_a_Program_Files_folder_stays_per_user_when_only_the_user_entry_names_it()
    {
        var exe = @"C:\Program Files\Own\AI-Usage.exe";

        Assert.Equal("/SILENT /CURRENTUSER", UpdateHost.BuildSetupArguments(exe, Roots, perMachine => perMachine ? null : @"C:\Program Files\Own"));
    }

    [Fact]
    public void Without_a_registry_entry_the_Program_Files_prefix_decides()
    {
        Assert.Equal("/SILENT /ALLUSERS", UpdateHost.BuildSetupArguments(@"C:\Program Files\AI-Usage\AI-Usage.exe", Roots, _ => null));
        Assert.Equal("/SILENT /CURRENTUSER", UpdateHost.BuildSetupArguments(@"D:\Tools\AI-Usage\AI-Usage.exe", Roots, _ => null));
    }

    // A connection that goes quiet after the headers must end the download, not hold it forever.
    [Fact]
    public async Task A_download_whose_body_stalls_fails_after_the_stall_limit()
    {
        var destination = TestPaths.GetPath("update-download", ".bin");
        try
        {
            var download = UpdateHost.DownloadAsync(
                "https://github.com/example/setup.exe", destination, new StalledBodyHandler(), TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(10), CancellationToken.None);

            var started = DateTime.UtcNow;
            var finished = await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(8)));

            Assert.Same(download, finished);
            Assert.False(await download);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    // A slow link that keeps delivering is not a stall, however long the whole body takes.
    [Fact]
    public async Task A_slow_download_that_keeps_delivering_chunks_succeeds_past_the_stall_limit()
    {
        var destination = TestPaths.GetPath("update-download", ".bin");
        try
        {
            var handler = new TrickleBodyHandler(chunks: 10, interval: TimeSpan.FromMilliseconds(300));

            var ok = await UpdateHost.DownloadAsync(
                "https://github.com/example/setup.exe", destination, handler, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10),
                CancellationToken.None);

            Assert.True(ok);
            Assert.Equal(10 * 1000, new FileInfo(destination).Length);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public async Task A_download_that_keeps_trickling_still_ends_at_the_overall_cap()
    {
        var destination = TestPaths.GetPath("update-download", ".bin");
        try
        {
            var handler = new TrickleBodyHandler(chunks: int.MaxValue, interval: TimeSpan.FromMilliseconds(100));

            var download = UpdateHost.DownloadAsync(
                "https://github.com/example/setup.exe", destination, handler, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1),
                CancellationToken.None);

            var finished = await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(8)));

            Assert.Same(download, finished);
            Assert.False(await download);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public async Task A_download_cancelled_by_the_caller_still_throws()
    {
        var destination = TestPaths.GetPath("update-download", ".bin");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateHost.DownloadAsync(
                "https://github.com/example/setup.exe", destination, new StalledBodyHandler(), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), cts.Token));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
    }

    private sealed class TrickleBodyHandler(int chunks, TimeSpan interval) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TrickleStream(chunks, interval)) });
    }

    /// <summary>Delivers 1000 bytes per read, one read every <c>interval</c>, for <c>chunks</c> reads.</summary>
    private sealed class TrickleStream(int chunks, TimeSpan interval) : Stream
    {
        private int _delivered;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_delivered >= chunks)
                return 0;
            await Task.Delay(interval, cancellationToken);
            _delivered++;
            buffer.Span[..1000].Fill(1);
            return 1000;
        }
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
