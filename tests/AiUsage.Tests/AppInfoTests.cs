using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class AppInfoTests
{
    [Fact]
    public void ArchiveUrlIsTheOneKnownConstantAndUsesHttps()
    {
        Assert.StartsWith("https://", AppInfo.ArchiveUrl);
        Assert.Equal("https://github.com/wbgcoding/AI-Usage", AppInfo.ArchiveUrl);
    }

    [Fact]
    public void IsInstalledIsFalseForATestHostRunningFromNeitherInstallRoot()
    {
        // The test host itself never runs from installer/AiUsage.iss's two possible roots, so this
        // is always false here - it exists to pin the "false unless proven otherwise" default.
        Assert.False(AppInfo.IsInstalled);
    }

    [Fact]
    public void IsInstalledAt_is_true_when_the_uninstaller_sits_next_to_the_exe()
    {
        var folder = TestPaths.CreateDirectory("ai-usage-appinfo");
        try
        {
            File.WriteAllText(Path.Combine(folder, "unins000.exe"), "");
            Assert.True(AppInfo.IsInstalledAt(Path.Combine(folder, "AI-Usage.exe"), @"D:\Profiles\x\Local", @"C:\Program Files"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void IsInstalledAt_matches_the_default_roots_only_as_whole_folders()
    {
        const string local = @"D:\Profiles\x\Local";
        const string pf = @"C:\Program Files";

        Assert.True(AppInfo.IsInstalledAt(@"C:\Program Files\AI-Usage\AI-Usage.exe", local, pf));
        Assert.True(AppInfo.IsInstalledAt(@"D:\Profiles\x\Local\Programs\AI-Usage\AI-Usage.exe", local, pf));
        Assert.False(AppInfo.IsInstalledAt(@"C:\Program Files\AI-Usage-old\AI-Usage.exe", local, pf));
        Assert.False(AppInfo.IsInstalledAt(@"D:\Profiles\x\Local\Programs\AI-Usage-old\AI-Usage.exe", local, pf));
        Assert.False(AppInfo.IsInstalledAt(null, local, pf));
    }
}
