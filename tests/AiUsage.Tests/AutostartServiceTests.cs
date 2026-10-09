using System.Security.AccessControl;
using System.Security.Principal;
using AiUsage.Services;
using Microsoft.Win32;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Runs against a real, isolated HKCU key - never the real Run key, and always cleaned up afterward, on any
/// machine (HKCU needs no admin rights).
/// </summary>
public sealed class AutostartServiceTests : IDisposable
{
    private const string TestKeyPath = @"Software\AI-Usage-Test";
    private const string TestApprovedKeyPath = TestKeyPath + @"\StartupApproved";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(TestKeyPath, throwOnMissingSubKey: false);

    [Fact]
    public void IsEnabledIsFalseWhenTheValueHasNeverBeenSet() =>
        Assert.False(AutostartService.IsEnabled(TestKeyPath));

    [Fact]
    public void EnableWritesTheQuotedPathWithTheTrayArgumentAndIsEnabledSeesIt()
    {
        AutostartService.Enable(TestKeyPath, @"C:\Program Files\AI-Usage\AI-Usage.exe");

        Assert.True(AutostartService.IsEnabled(TestKeyPath, currentExePath: @"C:\Program Files\AI-Usage\AI-Usage.exe"));
        using var key = Registry.CurrentUser.OpenSubKey(TestKeyPath);
        Assert.Equal("\"C:\\Program Files\\AI-Usage\\AI-Usage.exe\" --tray", key!.GetValue("AI-Usage"));
    }

    private static void WriteApprovalMarker(byte firstByte)
    {
        using var key = Registry.CurrentUser.CreateSubKey(TestApprovedKeyPath);
        var marker = new byte[12];
        marker[0] = firstByte;
        key.SetValue("AI-Usage", marker, RegistryValueKind.Binary);
    }

    [Fact]
    public void IsEnabledIsFalseWhenTaskManagerDisabledTheEntry()
    {
        AutostartService.Enable(TestKeyPath, @"C:\x.exe");
        WriteApprovalMarker(0x03);

        Assert.False(AutostartService.IsEnabled(TestKeyPath, TestApprovedKeyPath, @"C:\x.exe"));
    }

    [Fact]
    public void IsEnabledStaysTrueWhenTaskManagerMarkedTheEntryEnabled()
    {
        AutostartService.Enable(TestKeyPath, @"C:\x.exe");
        WriteApprovalMarker(0x02);

        Assert.True(AutostartService.IsEnabled(TestKeyPath, TestApprovedKeyPath, @"C:\x.exe"));
    }

    [Fact]
    public void IsEnabledIsFalseWithoutTheRunValueEvenIfTheMarkerSaysEnabled()
    {
        WriteApprovalMarker(0x02);

        Assert.False(AutostartService.IsEnabled(TestKeyPath, TestApprovedKeyPath, @"C:\x.exe"));
    }

    [Fact]
    public void EnableClearsATaskManagerDisableMarkerSoTheChoiceTakesEffect()
    {
        AutostartService.Enable(TestKeyPath, @"C:\x.exe");
        WriteApprovalMarker(0x03);

        Assert.True(AutostartService.Enable(TestKeyPath, @"C:\x.exe", TestApprovedKeyPath));

        using var key = Registry.CurrentUser.OpenSubKey(TestApprovedKeyPath);
        Assert.Null(key!.GetValue("AI-Usage"));
        Assert.True(AutostartService.IsEnabled(TestKeyPath, TestApprovedKeyPath, @"C:\x.exe"));
    }

    [Fact]
    public void IsEnabledIsFalseForAnEntryThatStartsAnotherProgramFile()
    {
        AutostartService.Enable(TestKeyPath, @"C:\Old\AI-Usage.exe");

        Assert.False(AutostartService.IsEnabled(TestKeyPath, currentExePath: @"C:\New\AI-Usage.exe"));
        Assert.True(AutostartService.IsEnabled(TestKeyPath, currentExePath: @"c:\old\ai-usage.EXE"));
    }

    [Fact]
    public void IsEnabledReadsTheRegistryAndChangesNothing()
    {
        AutostartService.Enable(TestKeyPath, @"C:\Old\AI-Usage.exe");

        AutostartService.IsEnabled(TestKeyPath, currentExePath: @"C:\New\AI-Usage.exe");

        using var key = Registry.CurrentUser.OpenSubKey(TestKeyPath);
        Assert.Equal("\"C:\\Old\\AI-Usage.exe\" --tray", key!.GetValue("AI-Usage"));
    }

    [Fact]
    public void RepairIfMovedPointsAnEntryForAMissingFileAtTheCurrentCopy()
    {
        AutostartService.Enable(TestKeyPath, @"C:\Old\AI-Usage.exe");

        var repaired = AutostartService.RepairIfMoved(TestKeyPath, @"C:\New\AI-Usage.exe", _ => false);

        Assert.True(repaired);
        Assert.True(AutostartService.IsEnabled(TestKeyPath, currentExePath: @"C:\New\AI-Usage.exe"));
    }

    [Fact]
    public void RepairIfMovedLeavesAnEntryForAnotherCopyThatStillExists()
    {
        AutostartService.Enable(TestKeyPath, @"C:\Old\AI-Usage.exe");

        var repaired = AutostartService.RepairIfMoved(TestKeyPath, @"C:\New\AI-Usage.exe", _ => true);

        Assert.False(repaired);
        Assert.True(AutostartService.IsEnabled(TestKeyPath, currentExePath: @"C:\Old\AI-Usage.exe"));
    }

    [Fact]
    public void RepairIfMovedDoesNothingWithoutAnEntryOrWhenItAlreadyMatches()
    {
        Assert.False(AutostartService.RepairIfMoved(TestKeyPath, @"C:\New\AI-Usage.exe", _ => false));
        using (var key = Registry.CurrentUser.OpenSubKey(TestKeyPath))
            Assert.Null(key?.GetValue("AI-Usage"));

        AutostartService.Enable(TestKeyPath, @"C:\New\AI-Usage.exe");
        Assert.False(AutostartService.RepairIfMoved(TestKeyPath, @"C:\New\AI-Usage.exe", _ => false));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\AI-Usage\\AI-Usage.exe\" --tray", "C:\\Program Files\\AI-Usage\\AI-Usage.exe")]
    [InlineData("C:\\Tools\\AI-Usage.exe --tray", "C:\\Tools\\AI-Usage.exe")]
    [InlineData("C:\\Tools\\AI-Usage.exe", "C:\\Tools\\AI-Usage.exe")]
    [InlineData("\"unterminated", null)]
    [InlineData("", null)]
    public void ExtractExePathReadsTheProgramOutOfARunValue(string value, string? expected)
    {
        Assert.Equal(expected, AutostartService.ExtractExePath(value));
    }

    [Fact]
    public void DisableRemovesTheValueEntirelyRatherThanBlankingIt()
    {
        AutostartService.Enable(TestKeyPath, @"C:\x.exe");

        AutostartService.Disable(TestKeyPath);

        Assert.False(AutostartService.IsEnabled(TestKeyPath));
        using var key = Registry.CurrentUser.OpenSubKey(TestKeyPath);
        Assert.Null(key!.GetValue("AI-Usage"));
    }

    [Fact]
    public void DisablingTwiceInARowNeverThrows()
    {
        AutostartService.Enable(TestKeyPath, @"C:\x.exe");
        AutostartService.Disable(TestKeyPath);

        var ex = Record.Exception(() => AutostartService.Disable(TestKeyPath));

        Assert.Null(ex);
    }

    [Fact]
    public void DisablingWithoutAPriorEnableNeverThrows()
    {
        var ex = Record.Exception(() => AutostartService.Disable(TestKeyPath));

        Assert.Null(ex);
    }

    /// <summary>Denies this same user the very access Enable/Disable need on a key it owns - the
    /// owner keeps WRITE_DAC (the right to edit permissions) even after denying itself every other
    /// right, so this needs no admin elevation and works the same on any machine. Windows then
    /// refuses the write itself, proving Enable/Disable report the failure instead of throwing.</summary>
    [Fact]
    public void EnableAndDisableReturnFalseInsteadOfThrowingWhenWindowsDeniesTheWrite()
    {
        const string deniedKeyPath = TestKeyPath + @"\Denied";
        var identity = WindowsIdentity.GetCurrent().User!;
        var denyRule = new RegistryAccessRule(
            identity, RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete,
            AccessControlType.Deny);

        using (var key = Registry.CurrentUser.CreateSubKey(deniedKeyPath))
        {
            var security = key!.GetAccessControl();
            security.AddAccessRule(denyRule);
            key.SetAccessControl(security);
        }

        try
        {
            Assert.False(AutostartService.Enable(deniedKeyPath, @"C:\x.exe"));
            Assert.False(AutostartService.Disable(deniedKeyPath));
        }
        finally
        {
            // Re-grants the denied rights so Dispose's DeleteSubKeyTree can remove the key afterward.
            // Opened with exactly RegistryRights.ChangePermissions rather than writable:true - a
            // "writable" open also requests SetValue/CreateSubKey, which the deny rule above still
            // has in force at this point and would refuse; ChangePermissions is the one right the
            // owner of a key always keeps regardless of what it has denied itself everywhere else.
            using var key = Registry.CurrentUser.OpenSubKey(
                deniedKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!;
            var security = key.GetAccessControl();
            security.RemoveAccessRule(denyRule);
            key.SetAccessControl(security);
        }
    }
}
