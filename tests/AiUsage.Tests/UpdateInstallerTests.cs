using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class UpdateInstallerTests
{
    private const string ExeUrl = "https://github.com/wbgcoding/AI-Usage/releases/download/v9.0.0/AI-Usage.exe";
    private const string SigUrl = ExeUrl + ".sig";
    private const string SetupName = "Setup-AI-Usage-9.0.0.exe";
    private const string SetupUrl = "https://github.com/wbgcoding/AI-Usage/releases/download/v9.0.0/" + SetupName;

    private static readonly byte[] Payload = [.. "pretend program bytes"u8];

    private sealed class FakeHost : IUpdateHost
    {
        public bool IsInstalled { get; init; }

        public Architecture Architecture { get; init; } = Architecture.X64;

        public string WorkFolder { get; } = TestPaths.CreateDirectory("update-work");

        public string PublicKey { get; init; } = "";

        public Version RunningVersion { get; init; } = new(1, 0, 0);

        /// <summary>What the downloaded file reports as its version; null models a file without one.</summary>
        public Version? FileVersion { get; init; } = new(9, 0, 0, 0);

        public Version? ReadFileVersion(string path) => FileVersion;

        public Dictionary<string, byte[]> Files { get; } = [];

        public List<string> Downloads { get; } = [];

        public List<string> Ran { get; } = [];

        /// <summary>Runs while the host is handed the file, to model something touching it at that moment.</summary>
        public Action<string>? OnLaunch { get; set; }

        public List<byte[]> SeenAtLaunch { get; } = [];

        public byte[]? ReplacedWith { get; private set; }

        public Task<bool> DownloadAsync(string url, string destination, CancellationToken ct)
        {
            Downloads.Add(url);
            if (!Files.TryGetValue(url, out var bytes))
                return Task.FromResult(false);

            File.WriteAllBytes(destination, bytes);
            return Task.FromResult(true);
        }

        public void StartSetupAndExit(string setupPath)
        {
            OnLaunch?.Invoke(setupPath);
            SeenAtLaunch.Add(File.ReadAllBytes(setupPath));
            Ran.Add("setup:" + Path.GetFileName(setupPath));
        }

        public void ReplaceRunningAndRestart(byte[] verifiedExe)
        {
            ReplacedWith = verifiedExe;
            Ran.Add("replace");
        }
    }

    private static (string PublicKey, ECDsa Key) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key);
    }

    private static string Sign(ECDsa key, byte[] data) =>
        Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static UpdateCheck.Release PortableRelease() => new("v9.0.0", "https://github.com/wbgcoding/AI-Usage/releases/tag/v9.0.0",
    [
        new("AI-Usage.exe", ExeUrl), new("AI-Usage.exe.sig", SigUrl),
        new(SetupName, SetupUrl), new(SetupName + ".sig", SetupUrl + ".sig"),
    ]);

    private static FakeHost PortableHost(string publicKey, byte[] exeBytes, string signature)
    {
        var host = new FakeHost { IsInstalled = false, PublicKey = publicKey };
        host.Files[ExeUrl] = exeBytes;
        host.Files[SigUrl] = [.. System.Text.Encoding.ASCII.GetBytes(signature)];
        return host;
    }

    [Fact]
    public async Task AValidSignatureHandsAPortableUpdateToTheReplaceStep()
    {
        var (publicKey, key) = NewKey();
        var host = PortableHost(publicKey, Payload, Sign(key, Payload));

        var outcome = await new UpdateInstaller(host).InstallAsync(PortableRelease(), CancellationToken.None);

        Assert.Equal(UpdateOutcome.Started, outcome);
        Assert.Equal(["replace"], host.Ran);
        Assert.Equal(Payload, host.ReplacedWith);
        Assert.Empty(Directory.GetFiles(host.WorkFolder));
    }

    [Theory]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 9, 0, 0)]
    [InlineData(-1, 0, 0, 0)]
    public async Task AValidlySignedFileThatIsNotNewerThanTheRunningAppIsRefused(int major, int minor, int build, int revision)
    {
        var (publicKey, key) = NewKey();
        var host = new FakeHost
        {
            IsInstalled = false, PublicKey = publicKey,
            FileVersion = major < 0 ? null : new Version(major, minor, build, revision),
        };
        host.Files[ExeUrl] = Payload;
        host.Files[SigUrl] = [.. System.Text.Encoding.ASCII.GetBytes(Sign(key, Payload))];

        var logged = new List<string>();

        var outcome = await new UpdateInstaller(host, logged.Add).InstallAsync(PortableRelease(), CancellationToken.None);

        // A file that carries no readable version cannot be compared at all and stays unverified.
        Assert.Equal(major < 0 ? UpdateOutcome.NotVerified : UpdateOutcome.NotNewer, outcome);
        Assert.Empty(host.Ran);
        Assert.Empty(Directory.GetFiles(host.WorkFolder));
        if (major >= 0)
        {
            var line = Assert.Single(logged);
            Assert.Contains("1.0.0", line);
            Assert.Contains(new Version(major, minor, build, revision).ToString(), line);
            Assert.DoesNotContain(host.WorkFolder, line);
        }
    }

    [Theory]
    [InlineData(UpdateOutcome.NotNewer, "Update.NotNewer")]
    [InlineData(UpdateOutcome.NotVerified, "Update.NotVerified")]
    [InlineData(UpdateOutcome.DownloadFailed, "Update.NotLoaded")]
    [InlineData(UpdateOutcome.NoMatchingFile, "Update.NotLoaded")]
    public void EachRefusedOutcomeHasItsOwnMessage(UpdateOutcome outcome, string key)
    {
        Assert.Equal(key, AiUsage.Views.UpdateDialogs.MessageKey(outcome));
        Assert.NotEqual(key, LocalizationService.Instance[key]);
    }

    [Fact]
    public async Task AnOlderSetupIsRefusedForAnInstalledCopy()
    {
        var (publicKey, key) = NewKey();
        var host = new FakeHost { IsInstalled = true, PublicKey = publicKey, RunningVersion = new Version(2, 0, 0), FileVersion = new Version(1, 5, 0, 0) };
        host.Files[SetupUrl] = Payload;
        host.Files[SetupUrl + ".sig"] = [.. System.Text.Encoding.ASCII.GetBytes(Sign(key, Payload))];

        var outcome = await new UpdateInstaller(host).InstallAsync(PortableRelease(), CancellationToken.None);

        Assert.Equal(UpdateOutcome.NotNewer, outcome);
        Assert.Empty(host.Ran);
        Assert.Empty(Directory.GetFiles(host.WorkFolder));
    }

    [Fact]
    public async Task AFileWhoseVersionDiffersOnlyInTheFourthPartIsNewer()
    {
        var (publicKey, key) = NewKey();
        var host = new FakeHost { IsInstalled = false, PublicKey = publicKey, FileVersion = new Version(1, 0, 0, 1) };
        host.Files[ExeUrl] = Payload;
        host.Files[SigUrl] = [.. System.Text.Encoding.ASCII.GetBytes(Sign(key, Payload))];

        var outcome = await new UpdateInstaller(host).InstallAsync(PortableRelease(), CancellationToken.None);

        Assert.Equal(UpdateOutcome.Started, outcome);
    }

    [Fact]
    public async Task AFileChangedAfterVerificationIsNotWhatTheSetupRuns()
    {
        var (publicKey, key) = NewKey();
        var host = new FakeHost { IsInstalled = true, PublicKey = publicKey };
        host.Files[SetupUrl] = Payload;
        host.Files[SetupUrl + ".sig"] = [.. System.Text.Encoding.ASCII.GetBytes(Sign(key, Payload))];
        var overwriteFailed = false;
        var deleteFailed = false;
        host.OnLaunch = path =>
        {
            try { File.WriteAllBytes(path, [.. "evil"u8]); }
            catch (IOException) { overwriteFailed = true; }
            try { File.Delete(path); }
            catch (IOException) { deleteFailed = true; }
        };

        var outcome = await new UpdateInstaller(host).InstallAsync(PortableRelease(), CancellationToken.None);

        Assert.Equal(UpdateOutcome.Started, outcome);
        Assert.True(overwriteFailed);
        Assert.True(deleteFailed);
        Assert.Equal([Payload], host.SeenAtLaunch);
    }

    [Fact]
    public async Task AValidSignatureRunsTheSetupForAnInstalledCopy()
    {
        var (publicKey, key) = NewKey();
        var host = new FakeHost { IsInstalled = true, PublicKey = publicKey };
        host.Files[SetupUrl] = Payload;
        host.Files[SetupUrl + ".sig"] = [.. System.Text.Encoding.ASCII.GetBytes(Sign(key, Payload))];

        var outcome = await new UpdateInstaller(host).InstallAsync(PortableRelease(), CancellationToken.None);

        Assert.Equal(UpdateOutcome.Started, outcome);
        Assert.Equal(["setup:" + SetupName], host.Ran);
    }

    [Fact]
    public async Task AChangedFileIsNeverRunAndBothFilesAreDeleted()
    {
        var (publicKey, key) = NewKey();
        byte[] tampered = [.. Payload, 0x00];
        var host = PortableHost(publicKey, tampered, Sign(key, Payload));

        var outcome = await new UpdateInstaller(host).InstallAsync(PortableRelease(), CancellationToken.None);

        Assert.Equal(UpdateOutcome.NotVerified, outcome);
        Assert.Empty(host.Ran);
        Assert.Empty(Directory.GetFiles(host.WorkFolder));
    }

    [Fact]
    public async Task ASignatureFromAnotherKeyIsNeverRun()
    {
        var (publicKey, _) = NewKey();
        var (_, strangerKey) = NewKey();
        var host = PortableHost(publicKey, Payload, Sign(strangerKey, Payload));

        var outcome = await new UpdateInstaller(host).InstallAsync(PortableRelease(), CancellationToken.None);

        Assert.Equal(UpdateOutcome.NotVerified, outcome);
        Assert.Empty(host.Ran);
        Assert.Empty(Directory.GetFiles(host.WorkFolder));
    }

    [Fact]
    public async Task AReleaseWithoutASignatureFileLoadsAndRunsNothing()
    {
        var (publicKey, key) = NewKey();
        var host = PortableHost(publicKey, Payload, Sign(key, Payload));
        var release = new UpdateCheck.Release("v9.0.0", "https://github.com/x/y", [new("AI-Usage.exe", ExeUrl)]);

        var outcome = await new UpdateInstaller(host).InstallAsync(release, CancellationToken.None);

        Assert.Equal(UpdateOutcome.NotVerified, outcome);
        Assert.Empty(host.Ran);
        Assert.Empty(host.Downloads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all!")]
    [InlineData("AAAA")]
    public async Task AMissingOrBrokenSignatureFileIsNeverRun(string signature)
    {
        var (publicKey, _) = NewKey();
        var host = PortableHost(publicKey, Payload, signature);

        var outcome = await new UpdateInstaller(host).InstallAsync(PortableRelease(), CancellationToken.None);

        Assert.Equal(UpdateOutcome.NotVerified, outcome);
        Assert.Empty(host.Ran);
        Assert.Empty(Directory.GetFiles(host.WorkFolder));
    }

    [Fact]
    public async Task AnEmptyPublicKeyRefusesEvenAGoodSignature()
    {
        var (_, key) = NewKey();
        var host = PortableHost("", Payload, Sign(key, Payload));

        var outcome = await new UpdateInstaller(host).InstallAsync(PortableRelease(), CancellationToken.None);

        Assert.Equal(UpdateOutcome.NotVerified, outcome);
        Assert.Empty(host.Ran);
    }

    [Fact]
    public async Task AFailedDownloadRunsNothingAndLeavesNothingBehind()
    {
        var (publicKey, key) = NewKey();
        var host = PortableHost(publicKey, Payload, Sign(key, Payload));
        host.Files.Remove(SigUrl);

        var outcome = await new UpdateInstaller(host).InstallAsync(PortableRelease(), CancellationToken.None);

        Assert.Equal(UpdateOutcome.DownloadFailed, outcome);
        Assert.Empty(host.Ran);
        Assert.Empty(Directory.GetFiles(host.WorkFolder));
    }

    [Fact]
    public async Task AReleaseWithNoFileForThisCopyIsReportedAsSuch()
    {
        var host = new FakeHost { IsInstalled = false, Architecture = Architecture.Arm64 };

        var outcome = await new UpdateInstaller(host).InstallAsync(PortableRelease(), CancellationToken.None);

        Assert.Equal(UpdateOutcome.NoMatchingFile, outcome);
        Assert.Empty(host.Downloads);
    }

    [Fact]
    public async Task AnAssetOnAnotherHostIsNeverLoaded()
    {
        var (publicKey, key) = NewKey();
        var host = PortableHost(publicKey, Payload, Sign(key, Payload));
        var release = new UpdateCheck.Release("v9.0.0", "https://github.com/x/y",
            [new("AI-Usage.exe", "https://example.net/AI-Usage.exe"), new("AI-Usage.exe.sig", SigUrl)]);

        var outcome = await new UpdateInstaller(host).InstallAsync(release, CancellationToken.None);

        Assert.Equal(UpdateOutcome.DownloadFailed, outcome);
        Assert.Empty(host.Downloads);
        Assert.Empty(host.Ran);
    }

    [Fact]
    public void AssetsAreChosenByDeliveryFormAndProcessor()
    {
        var assets = PortableRelease().Assets!;
        UpdateCheck.ReleaseAsset[] withArm = [.. assets, new("AI-Usage-arm64.exe", "https://github.com/x/AI-Usage-arm64.exe")];

        Assert.Equal(SetupName, UpdateInstaller.PickAsset(assets, installed: true, Architecture.X64)?.Name);
        Assert.Equal(SetupName, UpdateInstaller.PickAsset(assets, installed: true, Architecture.Arm64)?.Name);
        Assert.Equal("AI-Usage.exe", UpdateInstaller.PickAsset(assets, installed: false, Architecture.X64)?.Name);
        Assert.Null(UpdateInstaller.PickAsset(assets, installed: false, Architecture.Arm64));
        Assert.Equal("AI-Usage-arm64.exe", UpdateInstaller.PickAsset(withArm, installed: false, Architecture.Arm64)?.Name);
        Assert.Null(UpdateInstaller.PickAsset([], installed: true, Architecture.X64));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/wbgcoding/AI-Usage/releases/assets/1", true)]
    [InlineData("https://github.com/wbgcoding/AI-Usage/releases/download/v1/AI-Usage.exe", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("https://evilgithub.com/x", false)]
    [InlineData("https://raw.githubusercontent.com/x", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheListedGithubHostsOverHttpsMayServeAFile(string? url, bool expected) =>
        Assert.Equal(expected, UpdateInstaller.IsAllowedUrl(url));
}

public class UpdateSignatureTests
{
    [Fact]
    public void AGoodSignatureVerifiesAndAnyOtherInputDoesNot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        byte[] data = [1, 2, 3, 4];
        var signature = Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        Assert.True(UpdateSignature.Verify(data, signature, publicKey));
        Assert.False(UpdateSignature.Verify([1, 2, 3, 5], signature, publicKey));
        Assert.False(UpdateSignature.Verify(data, signature, ""));
        Assert.False(UpdateSignature.Verify(data, "", publicKey));
        Assert.False(UpdateSignature.Verify(data, signature, "not a key"));
        Assert.False(UpdateSignature.Verify(data, "###", publicKey));
    }

    // The ASN.1 form of the same signature is a different byte layout: it must not be accepted
    // where the release tooling writes IEEE P1363.
    [Fact]
    public void ADerEncodedSignatureIsRefused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        byte[] data = [9, 9, 9];
        var der = Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        Assert.False(UpdateSignature.Verify(data, der, publicKey));
    }

    [Fact]
    public void TheShippedPublicKeyIsAValidP256Key()
    {
        Assert.False(string.IsNullOrWhiteSpace(UpdatePublicKey.Value));
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdatePublicKey.Value), out _);
        Assert.Equal(256, key.KeySize);
    }
}

public class PortableSwapTests
{
    [Fact]
    public void ReplaceMovesTheRunningFileAsideAndPutsTheNewOneInItsPlace()
    {
        var folder = TestPaths.CreateDirectory("swap");
        var running = Path.Combine(folder, "AI-Usage.exe");
        File.WriteAllText(running, "old");

        Assert.True(PortableSwap.Replace(running, [.. "new"u8]));

        Assert.Equal("new", File.ReadAllText(running));
        Assert.Equal("old", File.ReadAllText(running + ".old"));

        PortableSwap.DeleteLeftover(running);
        Assert.False(File.Exists(running + ".old"));
    }

    [Fact]
    public void ReplaceDoesNothingWhenTheRunningFileIsMissing()
    {
        var folder = TestPaths.CreateDirectory("swap");
        var running = Path.Combine(folder, "AI-Usage.exe");

        Assert.False(PortableSwap.Replace(running, [.. "new"u8]));

        Assert.False(File.Exists(running));
        Assert.False(File.Exists(running + ".old"));
    }

    [Fact]
    public void TheRestartedCopyWaitsForTheOldOneToEnd()
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true };
        using var previous = System.Diagnostics.Process.Start(start)!;

        UpdateHost.WaitForPreviousCopy([UpdateHost.AfterUpdateSwitch, previous.Id.ToString()]);

        Assert.True(previous.HasExited);
    }

    [Theory]
    [InlineData("--after-update", "not-a-number")]
    [InlineData("--after-update", "2147483000")]
    public void TheWaitReturnsAtOnceForAnUnknownOrBrokenProcessId(string switchName, string value)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        UpdateHost.WaitForPreviousCopy([switchName, value]);
        UpdateHost.WaitForPreviousCopy([]);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }
}
