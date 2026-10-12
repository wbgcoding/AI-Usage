using AiUsage.Io;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The run folder both elevated installers share: a new empty folder, nothing but the file
/// inside it when it runs, and a held file that really is the one the path names.</summary>
public class FreshRunFolderTests
{
    private static readonly byte[] Payload = [.. "payload"u8];

    [Fact]
    public void EachRunGetsANewEmptyFolderUnderTheRoot()
    {
        var root = TestPaths.CreateDirectory("fresh-root");

        var first = FreshRunFolder.TryCreate(root);
        var second = FreshRunFolder.TryCreate(root);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(Path.GetFullPath(root), Path.GetDirectoryName(first));
        Assert.Empty(Directory.GetFileSystemEntries(first!));
    }

    [Fact]
    public void OnlyTheNamedFileCountsAsHeldAlone()
    {
        var folder = FreshRunFolder.TryCreate(TestPaths.CreateDirectory("fresh-holds"))!;
        var file = Path.Combine(folder, "setup.exe");
        File.WriteAllBytes(file, Payload);
        Assert.True(FreshRunFolder.HoldsOnly(folder, file));

        File.WriteAllBytes(Path.Combine(folder, "version.dll"), Payload);
        Assert.False(FreshRunFolder.HoldsOnly(folder, file));
    }

    [Fact]
    public void ARemovalLeavesAFolderWithContentAlone()
    {
        var folder = FreshRunFolder.TryCreate(TestPaths.CreateDirectory("fresh-remove"))!;
        File.WriteAllBytes(Path.Combine(folder, "setup.exe"), Payload);

        FreshRunFolder.TryRemoveEmpty(folder);

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void TheHeldFileIsRecognisedAsTheFileAtThePath()
    {
        var folder = TestPaths.CreateDirectory("lease-plain");
        var path = Path.Combine(folder, "file.exe");
        File.WriteAllBytes(path, Payload);
        using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.True(FreshRunFolder.LeaseIsAt(lease, path));
    }

    [Fact]
    public void AHeldFileThatIsNotTheOneAtThePathIsRefused()
    {
        var folder = TestPaths.CreateDirectory("lease-other");
        var held = Path.Combine(folder, "held.exe");
        var named = Path.Combine(folder, "named.exe");
        File.WriteAllBytes(held, Payload);
        File.WriteAllBytes(named, Payload);
        using var lease = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.False(FreshRunFolder.LeaseIsAt(lease, named));
    }

    [Fact]
    public void AFileSwappedForALinkAfterTheFirstCheckIsRefused()
    {
        var elsewhere = TestPaths.CreateDirectory("lease-target");
        var real = Path.Combine(elsewhere, "setup.exe");
        File.WriteAllBytes(real, Payload);
        var folder = TestPaths.CreateDirectory("lease-link");
        var link = Path.Combine(folder, "setup.exe");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // this account may not create file links, so the case cannot be set up here
        }

        using var lease = new FileStream(link, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.False(FreshRunFolder.LeaseIsAt(lease, link));
    }
}
