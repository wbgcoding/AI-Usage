using AiUsage.Storage;

namespace AiUsage.Tests;

public class AppPathsTests : IDisposable
{
    [Fact]
    public void SanitizeAccountKeyForFileName_leaves_no_path_characters()
    {
        Assert.Equal("claude-2", AppPaths.SanitizeAccountKeyForFileName("claude#2"));
        Assert.Equal("---x", AppPaths.SanitizeAccountKeyForFileName("../x"));
    }

    [Fact]
    public void SanitizeAccountKeyForFileName_of_an_empty_key_is_not_empty()
    {
        var name = AppPaths.SanitizeAccountKeyForFileName("");

        Assert.NotEmpty(name);
    }

    [Fact]
    public void SanitizeAccountKeyForFileName_of_two_keys_differing_only_in_forbidden_characters_differs()
    {
        // Both "!!!" and "###" flatten to the same run of dashes under the plain letter/digit/dash
        // mapping - they must still end up with different names, or WebViewHost would resolve them
        // onto the same session folder.
        var first = AppPaths.SanitizeAccountKeyForFileName("!!!");
        var second = AppPaths.SanitizeAccountKeyForFileName("###");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ResolveDataDirectory_uses_the_user_profile_folder_whenever_it_is_writable()
    {
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();

        var resolved = AppPaths.ResolveDataDirectory(appDataRoot, tempRoot);

        Assert.Equal(Path.Combine(appDataRoot, "AI-Usage"), resolved);
    }

    [Fact]
    public void ResolveDataDirectory_ignores_a_data_folder_next_to_the_running_copy()
    {
        // A copy started from its own folder used to keep its data beside itself. Such a folder can
        // still be lying around from an older build; it must not pull the app away from the profile.
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "data"));

        var resolved = AppPaths.ResolveDataDirectory(appDataRoot, tempRoot);

        Assert.Equal(Path.Combine(appDataRoot, "AI-Usage"), resolved);
    }

    [Fact]
    public void ResolveDataDirectory_falls_back_to_temp_when_the_profile_folder_is_not_writable()
    {
        // A file standing in for "AppData" makes that candidate fail regardless of the running
        // account's real permissions (same trick as SettingsStoreTests).
        var appDataRoot = TestPaths.GetPath("ai-usage-not-a-dir");
        File.WriteAllText(appDataRoot, "not a directory");
        var tempRoot = TempDirectory();
        try
        {
            var resolved = AppPaths.ResolveDataDirectory(appDataRoot, tempRoot);

            Assert.Equal(Path.Combine(tempRoot, "AI-Usage"), resolved);
        }
        finally
        {
            File.Delete(appDataRoot);
        }
    }

    [Fact]
    public void ResolveDataDirectory_follows_a_pointer_file_naming_a_writable_directory()
    {
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();
        var pointedDirectory = TempDirectory();
        Directory.CreateDirectory(Path.Combine(appDataRoot, "AI-Usage"));
        File.WriteAllText(Path.Combine(appDataRoot, "AI-Usage", "location.txt"), pointedDirectory);

        var resolved = AppPaths.ResolveDataDirectory(appDataRoot, tempRoot);

        Assert.Equal(pointedDirectory, resolved);
    }

    [Fact]
    public void ResolveDataDirectory_falls_back_to_the_profile_folder_when_the_pointed_directory_is_not_writable()
    {
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();
        // A file standing in for the pointed directory makes it fail to be a writable directory,
        // regardless of the running account's real permissions (same trick used elsewhere here).
        var pointedPath = TestPaths.GetPath("ai-usage-not-a-dir");
        File.WriteAllText(pointedPath, "not a directory");
        Directory.CreateDirectory(Path.Combine(appDataRoot, "AI-Usage"));
        File.WriteAllText(Path.Combine(appDataRoot, "AI-Usage", "location.txt"), pointedPath);
        try
        {
            var resolved = AppPaths.ResolveDataDirectory(appDataRoot, tempRoot);

            Assert.Equal(Path.Combine(appDataRoot, "AI-Usage"), resolved);
        }
        finally
        {
            File.Delete(pointedPath);
        }
    }

    [Fact]
    public void ResolveDataDirectory_ignores_a_pointer_file_naming_a_folder_under_temp()
    {
        // A pointer that resolved into the system temp directory has been observed in practice -
        // it must fall back to the profile folder rather than being followed like a deliberate move.
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();
        var pointedDirectory = Path.Combine(tempRoot, "somewhere");
        Directory.CreateDirectory(pointedDirectory);
        Directory.CreateDirectory(Path.Combine(appDataRoot, "AI-Usage"));
        File.WriteAllText(Path.Combine(appDataRoot, "AI-Usage", "location.txt"), pointedDirectory);

        var resolved = AppPaths.ResolveDataDirectory(appDataRoot, tempRoot);

        Assert.Equal(Path.Combine(appDataRoot, "AI-Usage"), resolved);
    }

    [Fact]
    public void ResolveDataDirectory_ignores_a_pointer_file_naming_the_temp_root_itself()
    {
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();
        Directory.CreateDirectory(Path.Combine(appDataRoot, "AI-Usage"));
        File.WriteAllText(Path.Combine(appDataRoot, "AI-Usage", "location.txt"), tempRoot);

        var resolved = AppPaths.ResolveDataDirectory(appDataRoot, tempRoot);

        Assert.Equal(Path.Combine(appDataRoot, "AI-Usage"), resolved);
    }

    [Fact]
    public void ResolveDataDirectory_ignores_a_pointer_file_naming_a_path_with_an_embedded_NUL()
    {
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();
        Directory.CreateDirectory(Path.Combine(appDataRoot, "AI-Usage"));
        File.WriteAllText(Path.Combine(appDataRoot, "AI-Usage", "location.txt"), Path.Combine(TempDirectory(), "x\0y"));

        var resolved = AppPaths.ResolveDataDirectory(appDataRoot, tempRoot);

        Assert.Equal(Path.Combine(appDataRoot, "AI-Usage"), resolved);
    }

    [Fact]
    public void ResolveDataDirectory_ignores_a_pointer_file_naming_a_relative_path()
    {
        // A relative path would resolve against the process's current working directory instead of
        // being a deliberate, unambiguous redirect - treated the same as no pointer file at all.
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();
        Directory.CreateDirectory(Path.Combine(appDataRoot, "AI-Usage"));
        File.WriteAllText(Path.Combine(appDataRoot, "AI-Usage", "location.txt"), "relative-folder");

        var resolved = AppPaths.ResolveDataDirectory(appDataRoot, tempRoot);

        Assert.Equal(Path.Combine(appDataRoot, "AI-Usage"), resolved);
    }

    [Fact]
    public void ClearOverrideForTests_never_lets_DataDirectory_fall_back_to_the_real_profile_folder()
    {
        // Reproduces exactly the sequence the ChooseDataFolder* tests in SettingsViewModelTests run
        // around the production SetOverride call. AppPaths.DataDirectory is a process-wide static
        // shared with every other test in this run, so clearing this test's own override must never
        // leave the *next* test able to resolve the real %APPDATA%\AI-Usage folder - that used to
        // happen whenever this was the first test in the process to touch DataDirectory at all, and
        // showed up as stray "Ignored a data-folder pointer..." lines in the real app.log.
        var probe = TempDirectory();
        var realDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AI-Usage");

        AppPaths.SetOverride(probe);
        Assert.Equal(probe, AppPaths.DataDirectory);

        AppPaths.ClearOverrideForTests();

        Assert.False(string.Equals(realDataDirectory, AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith(TestPaths.Root, AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyDataDirectory_recognizes_the_default_profile_folder_and_the_temp_fallback()
    {
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();

        Assert.Equal(AppPaths.DataFolderKind.Default,
            AppPaths.ClassifyDataDirectory(Path.Combine(appDataRoot, "AI-Usage"), appDataRoot, tempRoot));
        Assert.Equal(AppPaths.DataFolderKind.Fallback,
            AppPaths.ClassifyDataDirectory(Path.Combine(tempRoot, "AI-Usage"), appDataRoot, tempRoot));
    }

    [Fact]
    public void ClassifyDataDirectory_ignores_a_trailing_separator_on_the_pointed_folder()
    {
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();

        Assert.Equal(AppPaths.DataFolderKind.Default,
            AppPaths.ClassifyDataDirectory(Path.Combine(appDataRoot, "AI-Usage") + Path.DirectorySeparatorChar, appDataRoot, tempRoot));
        Assert.Equal(AppPaths.DataFolderKind.Fallback,
            AppPaths.ClassifyDataDirectory(Path.Combine(tempRoot, "AI-Usage") + Path.AltDirectorySeparatorChar, appDataRoot, tempRoot));
    }

    [Fact]
    public void SameDirectory_compares_full_paths_without_case_or_a_trailing_separator()
    {
        var folder = TempDirectory();

        Assert.True(AppPaths.SameDirectory(folder, folder + Path.DirectorySeparatorChar));
        Assert.True(AppPaths.SameDirectory(folder.ToUpperInvariant(), Path.Combine(folder, "sub", "..")));
        Assert.False(AppPaths.SameDirectory(folder, folder + "2"));
    }

    [Fact]
    public void ClassifyDataDirectory_treats_anything_else_as_a_chosen_folder()
    {
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();
        var chosenDirectory = TempDirectory();

        Assert.Equal(AppPaths.DataFolderKind.Chosen,
            AppPaths.ClassifyDataDirectory(chosenDirectory, appDataRoot, tempRoot));
    }

    private static DriveType DriveOf(string root) => root.ToUpperInvariant() switch
    {
        @"N:\" => DriveType.Network,
        @"R:\" => DriveType.Removable,
        _ => DriveType.Fixed,
    };

    [Theory]
    [InlineData(@"\\nas\share\AI-Usage", AppPaths.FolderRisk.Unc)]
    [InlineData(@"\\?\UNC\nas\share\AI-Usage", AppPaths.FolderRisk.Unc)]
    [InlineData(@"N:\data\AI-Usage", AppPaths.FolderRisk.NetworkDrive)]
    [InlineData(@"R:\AI-Usage", AppPaths.FolderRisk.RemovableDrive)]
    [InlineData(@"C:\Profiles\sample\OneDrive\AI-Usage", AppPaths.FolderRisk.CloudSync)]
    [InlineData(@"C:\Work\Dropbox\AI-Usage", AppPaths.FolderRisk.CloudSync)]
    [InlineData(@"D:\google drive\AI-Usage", AppPaths.FolderRisk.CloudSync)]
    [InlineData(@"D:\My Drive\x", AppPaths.FolderRisk.CloudSync)]
    [InlineData(@"C:\Profiles\sample\iCloudDrive\x", AppPaths.FolderRisk.CloudSync)]
    [InlineData(@"C:\Data\AI-Usage", AppPaths.FolderRisk.None)]
    [InlineData(@"D:\Dropbox-Backup\AI-Usage", AppPaths.FolderRisk.None)]
    [InlineData(@"C:\Profiles\sample\OneDrive2\x", AppPaths.FolderRisk.None)]
    public void ClassifyFolderRisk_tells_the_five_kinds_of_folder_apart(string path, AppPaths.FolderRisk expected) =>
        Assert.Equal(expected, AppPaths.ClassifyFolderRisk(path, DriveOf, [@"C:\Profiles\sample\OneDrive"]));

    [Fact]
    public void ClassifyFolderRisk_follows_the_configured_cloud_roots()
    {
        Assert.Equal(AppPaths.FolderRisk.CloudSync,
            AppPaths.ClassifyFolderRisk(@"E:\Company Sync\x", DriveOf, [@"E:\Company Sync"]));
        Assert.Equal(AppPaths.FolderRisk.None,
            AppPaths.ClassifyFolderRisk(@"E:\Company Sync\x", DriveOf, ["", "  "]));
    }

    [Fact]
    public void ClassifyFolderRisk_of_an_ordinary_local_folder_is_none_with_the_real_drives()
    {
        Assert.Equal(AppPaths.FolderRisk.None, AppPaths.ClassifyFolderRisk(Path.Combine(AppContext.BaseDirectory, "data")));
    }

    [Fact]
    public void ResolveDataDirectory_reports_a_pointed_folder_that_is_not_available_and_logs_it()
    {
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();
        var pointedPath = TestPaths.GetPath("ai-usage-unplugged");
        File.WriteAllText(pointedPath, "not a directory");
        Directory.CreateDirectory(Path.Combine(appDataRoot, "AI-Usage"));
        File.WriteAllText(Path.Combine(appDataRoot, "AI-Usage", "location.txt"), pointedPath);
        try
        {
            var resolved = AppPaths.ResolveDataDirectory(appDataRoot, tempRoot, out var unreachable);

            Assert.Equal(Path.Combine(appDataRoot, "AI-Usage"), resolved);
            Assert.Equal(pointedPath, unreachable);
            var log = Directory.EnumerateFiles(Path.Combine(appDataRoot, "AI-Usage", "logs"), "*.log").Single();
            Assert.Contains("is not available", File.ReadAllText(log));
        }
        finally
        {
            File.Delete(pointedPath);
        }
    }

    [Fact]
    public void ResolveDataDirectory_reports_nothing_when_the_pointed_folder_works_or_no_pointer_exists()
    {
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();
        AppPaths.ResolveDataDirectory(appDataRoot, tempRoot, out var withoutPointer);
        var pointed = TempDirectory();
        Directory.CreateDirectory(Path.Combine(appDataRoot, "AI-Usage"));
        File.WriteAllText(Path.Combine(appDataRoot, "AI-Usage", "location.txt"), pointed);
        AppPaths.ResolveDataDirectory(appDataRoot, tempRoot, out var withWorkingPointer);

        Assert.Null(withoutPointer);
        Assert.Null(withWorkingPointer);
    }

    [Fact]
    public void A_chosen_data_folder_gets_the_marker_the_uninstaller_looks_for()
    {
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();
        var chosen = Path.Combine(TempDirectory(), "my-data");

        AppPaths.EnsureDataMarker(chosen, appDataRoot, tempRoot);
        AppPaths.EnsureDataMarker(chosen, appDataRoot, tempRoot);

        Assert.Equal("ai-usage-data.marker", AppPaths.DataMarkerFileName);
        var marker = Path.Combine(chosen, AppPaths.DataMarkerFileName);
        Assert.True(File.Exists(marker));
        Assert.StartsWith("AI-Usage data folder", File.ReadAllText(marker), StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_and_the_fallback_folder_get_no_marker()
    {
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();
        var defaultFolder = Directory.CreateDirectory(Path.Combine(appDataRoot, "AI-Usage")).FullName;
        var fallbackFolder = Directory.CreateDirectory(Path.Combine(tempRoot, "AI-Usage")).FullName;

        AppPaths.EnsureDataMarker(defaultFolder, appDataRoot, tempRoot);
        AppPaths.EnsureDataMarker(fallbackFolder, appDataRoot, tempRoot);

        Assert.False(File.Exists(Path.Combine(defaultFolder, AppPaths.DataMarkerFileName)));
        Assert.False(File.Exists(Path.Combine(fallbackFolder, AppPaths.DataMarkerFileName)));
    }

    [Fact]
    public void A_folder_that_cannot_be_written_is_left_without_a_marker_and_without_an_error()
    {
        var appDataRoot = TempDirectory();
        var tempRoot = TempDirectory();
        var blocker = Path.Combine(TempDirectory(), "file");
        File.WriteAllText(blocker, "x");

        AppPaths.EnsureDataMarker(Path.Combine(blocker, "data"), appDataRoot, tempRoot);

        Assert.False(Directory.Exists(Path.Combine(blocker, "data")));
    }

    private readonly List<DisposableTestDirectory> _tempDirectories = [];

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-apppaths");
        _tempDirectories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }
}
