using Xunit;

namespace AiUsage.Tests;

/// <summary>Source checks on the installer scripts: a stalled runtime download ends the run, the
/// uninstall data question covers both data folders, and "app is running" means the app's own window.
/// The scripts cannot be executed in a unit test (a download, a real uninstall), so what they must
/// contain is pinned here and the behaviour is checked on the test machine.</summary>
public class InstallerSafetyTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot.Find(), .. parts]));

    [Fact]
    public void The_runtime_download_reads_with_a_stall_limit_and_an_overall_deadline()
    {
        var script = Read("installer", "RuntimeSetup.ps1");

        Assert.Contains("$StallSeconds = 60", script);
        Assert.Contains("$DownloadLimitMinutes = 10", script);
        // The wait on the read is bounded; the cancellation token of the read is not trusted.
        Assert.Contains("$task = $Stream.ReadAsync($Buffer, 0, $Buffer.Length)", script);
        Assert.Contains("$task.Wait($StallSeconds * 1000)", script);
        Assert.DoesNotContain("CancellationTokenSource", script);
        Assert.Contains("[DateTime]::UtcNow -gt $deadline", script);
        // The blocking read that had no limit is gone.
        Assert.DoesNotContain("$source.Read($buffer", script);
    }

    [Fact]
    public void The_download_loop_uses_the_limited_read()
    {
        var script = Read("installer", "RuntimeSetup.ps1");

        Assert.Contains("Read-WithTimeout $source $buffer", script);
    }

    [Fact]
    public void The_uninstall_data_choice_removes_the_roaming_and_the_local_folder_and_names_both()
    {
        var iss = Read("installer", "AiUsage.iss");

        Assert.Contains("DelTree(DataDir, True, True, True);", iss);
        Assert.Contains("DelTree(LocalDir, True, True, True);", iss);
        Assert.Contains("LocalDir := ExpandConstant('{localappdata}\\{#AppName}');", iss);
        foreach (var language in new[] { "en", "de" })
        {
            var line = iss.Split('\n').Single(l => l.StartsWith(language + ".RemoveDataQuestion=", StringComparison.Ordinal));
            Assert.Contains("%APPDATA%", line);
            Assert.Contains("%LOCALAPPDATA%", line);
        }

        Assert.Contains(@"en.RemoveDataQuestion=Also remove {#AppName}'s saved settings, history and browser sign-ins for the current user (%APPDATA%\{#AppName}\ and %LOCALAPPDATA%\{#AppName}\)?", iss);
        Assert.Contains(@"de.RemoveDataQuestion=Sollen auch die gespeicherten Einstellungen, der Verlauf und die Browser-Anmeldungen von {#AppName} für den aktuellen Benutzer (%APPDATA%\{#AppName}\ und %LOCALAPPDATA%\{#AppName}\) entfernt werden?", iss);
    }

    [Fact]
    public void The_uninstall_reads_the_moved_data_folder_before_deleting_and_removes_only_the_apps_own_entries()
    {
        var iss = Read("installer", "AiUsage.iss");

        var pointer = iss.IndexOf("MovedDir := MovedDataDir(", StringComparison.Ordinal);
        var deleteDefault = iss.IndexOf("DelTree(DataDir, True, True, True);", StringComparison.Ordinal);
        Assert.True(pointer > 0 && pointer < deleteDefault, "the pointer file must be read before the default folder is deleted");
        Assert.Contains("RemoveOwnDataEntries(MovedDir);", iss);

        // The moved folder is never deleted as a whole tree; the folder itself goes only when empty.
        Assert.DoesNotContain("DelTree(MovedDir", iss);
        Assert.DoesNotContain("DelTree(Folder,", iss);
        Assert.DoesNotContain("DelTree(AddBackslash(Folder),", iss);
        var start = iss.IndexOf("procedure RemoveOwnDataEntries", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = iss[start..iss.IndexOf("\nend;", start, StringComparison.Ordinal)];
        Assert.Contains("RemoveDir(Folder);", body);
        foreach (var name in new[] { "settings.json", "stats.db", "stats.db-wal", "stats.db-shm", "notifications.json", "status.json", "project-colors.json", "location" + ".txt" })
            Assert.Contains($"DeleteOwnFile(Folder, '{name}');", body);
        Assert.Contains("DeleteOwnFiles(Folder, 'history-*.jsonl');", body);
        Assert.Contains("DeleteOwnFolders(Folder, 'logs');", body);
        Assert.DoesNotContain("'*'", body);
        Assert.DoesNotContain("'*.*'", body);
    }

    [Fact]
    public void The_readme_describes_the_uninstall_question()
    {
        var readme = Read("README.md");

        Assert.DoesNotContain("Uninstalling keeps your data.", readme);
        Assert.Contains("Uninstalling asks whether to remove your data too", readme);
    }

    [Fact]
    public void The_app_counts_as_running_only_for_a_window_of_its_own_wpf_host()
    {
        var iss = Read("installer", "AiUsage.iss");

        Assert.Contains("AppWasRunning := IsAppWindowOpen;", iss);
        Assert.Contains("Pos('HwndWrapper[{#AppName}', Copy(ClassName, 1, Len)) = 1", iss);
        // Every window of that title is looked at, not just the first one Windows finds.
        Assert.Contains("FindWindowExA(0, Wnd, 0, '{#AppName}')", iss);
        Assert.DoesNotContain("AppWasRunning := FindWindowA", iss);
    }
}
