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

    private static byte[] PeFile(ushort machine, int peOffset = 0x80, int length = 0x200)
    {
        var bytes = new byte[length];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(peOffset).CopyTo(bytes, 0x3C);
        if (peOffset >= 0 && peOffset <= length - 6)
        {
            bytes[peOffset] = (byte)'P';
            bytes[peOffset + 1] = (byte)'E';
            BitConverter.GetBytes(machine).CopyTo(bytes, peOffset + 4);
        }

        return bytes;
    }

    [Theory]
    [InlineData((ushort)0x8664)]
    [InlineData((ushort)0xAA64)]
    public void ReadPeMachine_returns_the_machine_of_the_header(ushort machine)
    {
        using var stream = new MemoryStream(PeFile(machine));

        Assert.Equal(machine, UpdateHost.ReadPeMachine(stream));
    }

    [Fact]
    public void ReadPeMachine_refuses_anything_that_is_not_a_well_formed_pe_file()
    {
        var notPe = PeFile(0x8664);
        notPe[0x80] = (byte)'X';
        var notMz = PeFile(0x8664);
        notMz[0] = (byte)'Z';

        foreach (var bytes in new[]
        {
            notPe, notMz, PeFile(0x8664, peOffset: 0x10), PeFile(0x8664, peOffset: -1), PeFile(0x8664, peOffset: 0x1FE),
            PeFile(0x8664, peOffset: int.MaxValue), [], new byte[0x20], PeFile(0x8664)[..0x82],
        })
        {
            using var stream = new MemoryStream(bytes);
            Assert.Null(UpdateHost.ReadPeMachine(stream));
        }
    }

    [Theory]
    [InlineData("AI-Usage Setup                    ", "AI-Usage Setup")]
    [InlineData("                                  ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("AI-Usage.dll", "AI-Usage.dll")]
    public void Version_texts_padded_with_blanks_compare_by_their_content(string? raw, string? expected)
    {
        Assert.Equal(expected, UpdateHost.NormalizeVersionText(raw));
    }

    [Fact]
    public void The_real_host_reads_the_machine_and_version_texts_of_a_real_program()
    {
        var host = new UpdateHost(() => { });
        var self = Environment.ProcessPath!;
        var expected = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => (ushort)0xAA64,
            _ => (ushort)0x8664,
        };

        Assert.Equal(expected, host.ReadPeMachine(self));
        Assert.Null(host.ReadPeMachine(Path.Combine(TestPaths.Root, "does-not-exist.exe")));
        Assert.Null(host.ReadOriginalFilename(Path.Combine(TestPaths.Root, "does-not-exist.exe")));
        Assert.Null(host.ReadFileDescription(Path.Combine(TestPaths.Root, "does-not-exist.exe")));
    }

    [Fact]
    public async Task A_download_never_writes_over_a_file_that_is_already_at_the_destination()
    {
        var destination = TestPaths.GetPath("update-download", ".bin");
        File.WriteAllText(destination, "planted");

        var ok = await UpdateHost.DownloadAsync(
            "https://github.com/example/setup.exe", destination, new TrickleBodyHandler(chunks: 1, interval: TimeSpan.Zero),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("planted", File.ReadAllText(destination));
    }

    private static string RunFolder(string workFolder, string name, bool old)
    {
        var folder = Directory.CreateDirectory(Path.Combine(workFolder, name)).FullName;
        File.WriteAllText(Path.Combine(folder, "AI-Usage.exe"), "x");
        if (old)
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddDays(-3));
        return folder;
    }

    [Fact]
    public void CleanUp_removes_stale_run_folders_and_keeps_recent_ones()
    {
        var work = TestPaths.CreateDirectory("update-cleanup");
        var stale = RunFolder(work, "a", old: true);
        var recent = RunFolder(work, "b", old: false);

        UpdateHost.CleanUpStaleFiles(work, DateTime.UtcNow);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public void CleanUp_goes_on_after_a_file_that_is_still_in_use()
    {
        var work = TestPaths.CreateDirectory("update-cleanup");
        var locked = Path.Combine(work, "a-locked.exe");
        var free = Path.Combine(work, "b-free.exe");
        File.WriteAllText(locked, "x");
        File.WriteAllText(free, "x");
        File.SetLastWriteTimeUtc(locked, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(free, DateTime.UtcNow.AddDays(-3));
        var stale = RunFolder(work, "zz", old: true);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            UpdateHost.CleanUpStaleFiles(work, DateTime.UtcNow);

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void CleanUp_goes_on_after_a_run_folder_that_is_still_in_use()
    {
        var work = TestPaths.CreateDirectory("update-cleanup");
        var lockedRun = RunFolder(work, "a", old: true);
        var otherRun = RunFolder(work, "b", old: true);

        using (new FileStream(Path.Combine(lockedRun, "AI-Usage.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
            UpdateHost.CleanUpStaleFiles(work, DateTime.UtcNow);

        Assert.True(Directory.Exists(lockedRun));
        Assert.False(Directory.Exists(otherRun));
    }

    [Fact]
    public void CleanUp_removes_a_link_in_the_work_folder_without_touching_what_it_points_at()
    {
        var work = TestPaths.CreateDirectory("update-cleanup");
        var target = TestPaths.CreateDirectory("update-link-target");
        File.WriteAllText(Path.Combine(target, "precious.txt"), "x");
        var link = Path.Combine(work, "link");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!)
        {
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
        }

        Directory.SetLastWriteTimeUtc(link, DateTime.UtcNow.AddDays(-3));

        UpdateHost.CleanUpStaleFiles(work, DateTime.UtcNow);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
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
