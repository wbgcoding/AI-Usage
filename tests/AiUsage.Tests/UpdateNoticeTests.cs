using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

public class UpdateNoticeTests
{
    private const string Page = "https://github.com/wbgcoding/AI-Usage/releases/tag/v9.0.0";
    private const string ExeUrl = "https://github.com/wbgcoding/AI-Usage/releases/download/v9.0.0/AI-Usage.exe";

    private sealed class RecordingHost(string publicKey, byte[] exe, string signature) : IUpdateHost
    {
        public bool IsInstalled => false;

        public Architecture Architecture => Architecture.X64;

        public string WorkFolder { get; } = TestPaths.CreateDirectory("notice-work");

        public string PublicKey => publicKey;

        public Version RunningVersion => new(1, 0, 0);

        public Version? ReadFileVersion(string path) => new(9, 0, 0, 0);

        public List<string> Ran { get; } = [];

        public Task<bool> DownloadAsync(string url, string destination, CancellationToken ct)
        {
            File.WriteAllBytes(destination, url.EndsWith(".sig", StringComparison.Ordinal) ? System.Text.Encoding.ASCII.GetBytes(signature) : exe);
            return Task.FromResult(true);
        }

        public void StartSetupAndExit(string setupPath) => Ran.Add("setup");

        public bool ReplaceRunningAndRestart(byte[] verifiedExe)
        {
            Ran.Add("replace");
            return true;
        }
    }

    private static UpdateCheck.Release Newer() => new("v9.0.0", Page,
        [new("AI-Usage.exe", ExeUrl), new("AI-Usage.exe.sig", ExeUrl + ".sig")]);

    [Fact]
    public void ANewerVersionKnownFromEarlierShowsTheNoticeWithoutANewCheck()
    {
        var settings = new AppSettings { KnownLatestTag = "v9.0.0", KnownLatestUrl = Page };

        var update = new UpdateNoticeViewModel(settings, _ => { });

        Assert.True(update.ShowNotice);
        Assert.Equal("9.0.0", update.AvailableVersion);
        Assert.Contains("9.0.0", update.NoticeText, StringComparison.Ordinal);
    }

    [Fact]
    public void LaterHidesTheNoticeButTheVersionStaysKnownForTheAboutPage()
    {
        var update = new UpdateNoticeViewModel(new AppSettings { KnownLatestTag = "v9.0.0", KnownLatestUrl = Page }, _ => { });

        update.Dismiss();

        Assert.False(update.ShowNotice);
        Assert.True(update.HasUpdate);
    }

    [Fact]
    public void NoNoticeWhenTheKnownVersionIsNotNewerOrCheckingIsOff()
    {
        Assert.False(new UpdateNoticeViewModel(new AppSettings { KnownLatestTag = "v0.0.1" }, _ => { }).ShowNotice);
        Assert.False(new UpdateNoticeViewModel(new AppSettings { KnownLatestTag = "v9.0.0", CheckForUpdates = false }, _ => { }).ShowNotice);
        Assert.False(new UpdateNoticeViewModel(new AppSettings(), _ => { }).ShowNotice);
    }

    [Fact]
    public async Task ADueCheckThatFindsANewerVersionShowsTheNoticeAndRemembersIt()
    {
        var settings = new AppSettings();
        var saves = 0;
        var update = new UpdateNoticeViewModel(settings, _ => saves++) { FetchLatestRelease = _ => Task.FromResult<UpdateCheck.Release?>(Newer()) };

        await update.CheckAsync(CancellationToken.None);

        Assert.True(update.ShowNotice);
        Assert.Equal("v9.0.0", settings.KnownLatestTag);
        Assert.True(saves > 0);
        Assert.False(update.IsKnownUpToDate);
    }

    [Fact]
    public async Task AFindingOfNothingNewerClearsTheNoticeAndMarksUpToDate()
    {
        var settings = new AppSettings { KnownLatestTag = "v9.0.0", KnownLatestUrl = Page };
        var update = new UpdateNoticeViewModel(settings, _ => { })
        {
            FetchLatestRelease = _ => Task.FromResult<UpdateCheck.Release?>(new("v0.0.1", Page)),
        };

        await update.CheckAsync(CancellationToken.None);

        Assert.False(update.HasUpdate);
        Assert.True(update.IsKnownUpToDate);
        Assert.Null(settings.KnownLatestTag);
    }

    [Fact]
    public async Task InstallingReadsTheNewestReleaseAndHandsAVerifiedFileOver()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] exe = [1, 2, 3];
        var signature = Convert.ToBase64String(key.SignData(exe, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var host = new RecordingHost(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), exe, signature);
        var update = new UpdateNoticeViewModel(new AppSettings(), _ => { }, () => host)
        {
            FetchLatestRelease = _ => Task.FromResult<UpdateCheck.Release?>(Newer()),
        };

        var result = await update.InstallAsync(CancellationToken.None);

        Assert.Equal(UpdateOutcome.Started, result.Outcome);
        Assert.Equal(["replace"], host.Ran);
        Assert.Equal(Page, result.ReleaseUrl);
        Assert.False(update.IsInstalling);
    }

    [Fact]
    public async Task InstallingNeverRunsAnythingWhenTheSignatureIsWrongAndNamesTheReleasePage()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] exe = [1, 2, 3];
        var signature = Convert.ToBase64String(stranger.SignData(exe, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var host = new RecordingHost(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), exe, signature);
        var update = new UpdateNoticeViewModel(new AppSettings(), _ => { }, () => host)
        {
            FetchLatestRelease = _ => Task.FromResult<UpdateCheck.Release?>(Newer()),
        };

        var result = await update.InstallAsync(CancellationToken.None);

        Assert.Equal(UpdateOutcome.NotVerified, result.Outcome);
        Assert.Empty(host.Ran);
        Assert.Equal(Page, result.ReleaseUrl);
    }

    [Fact]
    public async Task InstallingWithoutAReachableOrNewerReleaseRunsNothing()
    {
        var host = new RecordingHost("", [], "");
        var unreachable = new UpdateNoticeViewModel(new AppSettings(), _ => { }, () => host)
        {
            FetchLatestRelease = _ => Task.FromResult<UpdateCheck.Release?>(null),
        };
        var notNewer = new UpdateNoticeViewModel(new AppSettings(), _ => { }, () => host)
        {
            FetchLatestRelease = _ => Task.FromResult<UpdateCheck.Release?>(new("v0.0.1", Page)),
        };

        Assert.Equal(UpdateOutcome.DownloadFailed, (await unreachable.InstallAsync(CancellationToken.None)).Outcome);
        Assert.Equal(UpdateOutcome.DownloadFailed, (await notNewer.InstallAsync(CancellationToken.None)).Outcome);
        Assert.Empty(host.Ran);
    }
}

public class UpdateSigningKeyGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No repository root above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TheSigningKeyFolderIsIgnoredByGit()
    {
        var root = RepoRoot();
        Assert.Contains(".signing/", File.ReadAllLines(Path.Combine(root, ".gitignore")).Select(l => l.Trim()));

        // The real answer from git, where it is available: the key file itself counts as ignored.
        var start = new ProcessStartInfo("git", "check-ignore -q .signing/ai-usage-update.pem")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(start)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No git on this machine: the .gitignore line above is all that can be checked here.
        }
    }

    [Fact]
    public void NoPrivateKeyMaterialIsPartOfTheSource()
    {
        var root = RepoRoot();
        var marker = "BEGIN " + "PRIVATE KEY";
        var skipped = new[] { "bin", "obj", ".tmp", ".git", ".signing", "dist", "build", "TestResults" };
        var offenders = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => skipped.Contains(part)))
            .Where(f => Path.GetExtension(f) is ".cs" or ".bat" or ".md" or ".txt" or ".json" or ".pem" or ".xaml" or ".resx" or ".iss" or ".csproj")
            .Where(f => File.ReadAllText(f).Contains(marker, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(offenders.Count == 0, "Private key material in: " + string.Join(", ", offenders));
    }
}
