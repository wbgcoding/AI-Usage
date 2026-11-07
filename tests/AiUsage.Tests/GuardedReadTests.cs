using AiUsage.Io;

namespace AiUsage.Tests;

public sealed class GuardedReadTests
{
    private readonly string _root = TestPaths.CreateDirectory("guarded-read");

    [Theory]
    [InlineData(@"\\server\share\x")]
    [InlineData("//server/share/x")]
    [InlineData(@"\\?\UNC\server\share\x")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\??\UNC\server\share\x")]
    [InlineData(@"\??\C:\Windows\x")]
    [InlineData(@"\??\UNC\server\share")]
    [InlineData(@"\/server/share")]
    [InlineData("relative.txt")]
    [InlineData(@"\rooted")]
    [InlineData("")]
    [InlineData(null)]
    public void IsLocalPath_refuses_unc_relative_and_empty_paths(string? path)
    {
        Assert.False(GuardedRead.IsLocalPath(path));
        Assert.False(GuardedRead.IsLocalDrivePath(path));
    }

    [Fact]
    public void IsLocalPath_accepts_a_fully_qualified_drive_path()
    {
        Assert.True(GuardedRead.IsLocalPath(Path.Combine(_root, "a.txt")));
        Assert.True(GuardedRead.IsLocalDrivePath(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public void TryReadSmallText_reads_a_file_within_the_limit_and_drops_a_bom()
    {
        var path = Path.Combine(_root, "small.json");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}']);

        Assert.True(GuardedRead.TryReadSmallText(path, 16, out var text));
        Assert.Equal("{}", text);
    }

    [Fact]
    public void TryReadSmallText_refuses_an_oversized_missing_or_unc_file()
    {
        var big = Path.Combine(_root, "big.txt");
        File.WriteAllText(big, new string('x', 100));

        Assert.False(GuardedRead.TryReadSmallText(big, 99, out var text));
        Assert.Equal("", text);
        Assert.True(GuardedRead.TryReadSmallText(big, 100, out _));
        Assert.False(GuardedRead.TryReadSmallText(Path.Combine(_root, "none.txt"), 100, out _));
        if (AdminShare.Of(big) is { } unc)
            Assert.False(GuardedRead.TryReadSmallText(unc, 1000, out _));
    }

    [Fact]
    public void TryReadSmallText_sizes_its_buffer_by_the_file_not_by_the_limit()
    {
        var path = Path.Combine(_root, "tiny.txt");
        File.WriteAllText(path, "ok");

        // A buffer of limit + 1 bytes would overflow here; one sized by the file does not.
        Assert.True(GuardedRead.TryReadSmallText(path, int.MaxValue, out var text));
        Assert.Equal("ok", text);
    }

    [Fact]
    public void TryReadSmallText_refuses_a_symbolic_link()
    {
        var target = Path.Combine(_root, "target.txt");
        File.WriteAllText(target, "x");
        var link = Path.Combine(_root, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        Assert.False(GuardedRead.TryReadSmallText(link, 100, out _));
    }

    [Fact]
    public void OpenedFileIsTheChecked_accepts_the_same_file_and_refuses_another_one_or_a_swapped_in_link()
    {
        var checkedFile = Path.Combine(_root, "checked.txt");
        var otherFile = Path.Combine(_root, "other.txt");
        File.WriteAllText(checkedFile, "a");
        File.WriteAllText(otherFile, "b");

        using (var opened = File.OpenHandle(checkedFile))
        {
            Assert.True(GuardedRead.OpenedFileIsTheChecked(opened, checkedFile));
            Assert.True(GuardedRead.OpenedFileIsTheChecked(opened, checkedFile.ToUpperInvariant()));
            Assert.False(GuardedRead.OpenedFileIsTheChecked(opened, otherFile));
            Assert.False(GuardedRead.OpenedFileIsTheChecked(opened, Path.Combine(_root, "no-such-folder", "checked.txt")));
        }

        // The path was checked as a plain file, then replaced by a link to another file before the open.
        var swapped = Path.Combine(_root, "swapped.txt");
        try
        {
            File.CreateSymbolicLink(swapped, otherFile);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        using var followed = File.OpenHandle(swapped);
        Assert.False(GuardedRead.OpenedFileIsTheChecked(followed, swapped));
    }

    [Fact]
    public void TryReadSmallTextUnder_reads_below_the_root_and_refuses_a_linked_folder_on_the_way()
    {
        var root = Path.Combine(_root, "under-root");
        var outside = Path.Combine(_root, "under-outside");
        Directory.CreateDirectory(Path.Combine(root, "plain", "inner"));
        Directory.CreateDirectory(Path.Combine(outside, "inner"));
        File.WriteAllText(Path.Combine(root, "plain", "inner", "a.txt"), "plain");
        File.WriteAllText(Path.Combine(outside, "inner", "a.txt"), "outside");

        Assert.True(GuardedRead.TryReadSmallTextUnder(root, Path.Combine(root, "plain", "inner", "a.txt"), 100, out var text));
        Assert.Equal("plain", text);
        // Outside the root, or named through a dot-dot back out of it: never read.
        Assert.False(GuardedRead.TryReadSmallTextUnder(root, Path.Combine(outside, "inner", "a.txt"), 100, out _));
        Assert.False(GuardedRead.TryReadSmallTextUnder(root, Path.Combine(root, "..", "under-outside", "inner", "a.txt"), 100, out _));

        var link = Path.Combine(root, "linked");
        if (!TryMakeJunction(link, outside))
            return; // The OS refused to create the junction; nothing to prove here.

        try
        {
            // The leaf is a regular file, but a folder above it is a junction.
            Assert.False(GuardedRead.TryReadSmallTextUnder(root, Path.Combine(link, "inner", "a.txt"), 100, out _));
            Assert.True(GuardedRead.TryReadSmallText(Path.Combine(link, "inner", "a.txt"), 100, out _));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    internal static bool TryMakeJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
