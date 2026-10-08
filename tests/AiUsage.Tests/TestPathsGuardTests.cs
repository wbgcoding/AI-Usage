using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace AiUsage.Tests;

/// <summary>Every test-owned temp path must live under TestPaths.Root so the whole tree can be found
/// and swept away again - this fails if any other test source file still reaches into the system
/// temp folder directly instead of going through that shared helper.</summary>
public class TestPathsGuardTests
{
    private static readonly Regex CallPattern = new(@"(?<!\w)GetTempPath\(", RegexOptions.Compiled);
    private static readonly string[] SkippedFileNames = ["TestPaths.cs", "TestPathsGuardTests.cs"];

    // AppPathsTests.cs only ever passes explicit fake roots into ResolveDataDirectory - it never
    // touches the real LocationPointerFile, so it needs no override. This file only names the
    // pattern itself, never the real pointer file.
    private static readonly string[] PointerFileNoOverrideExemptFileNames = ["AppPathsTests.cs", "TestPathsGuardTests.cs"];

    private static readonly Regex PointerFilePattern = new(@"(?<!\w)(LocationPointerFile|location\.txt)(?!\w)", RegexOptions.Compiled);
    private static readonly Regex PointerOverridePattern = new(@"(?<!\w)SetPointerRootOverride(?!\w)", RegexOptions.Compiled);
    private static readonly Regex RealAutostartKeyPattern = new(
        @"(?<!\w)(RunKeyPath|StartupApprovedKeyPath)(?!\w)|Explorer\\StartupApproved", RegexOptions.Compiled);
    private static readonly Regex LeftoverFolderNamePattern = new(@"(?<!\w)aiusage-tests(?!\w)", RegexOptions.Compiled);

    [Fact]
    public void No_test_source_file_reaches_into_the_system_temp_folder_outside_the_shared_helper()
    {
        var sourceDirectory = TestsSourceDirectory();
        var offending = Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => !SkippedFileNames.Contains(Path.GetFileName(path)))
            .Where(path => CallPattern.IsMatch(File.ReadAllText(path)))
            .ToList();

        Assert.Empty(offending); // expected count: 0
    }

    [Fact]
    public void Every_test_source_file_touching_the_pointer_file_installs_the_override()
    {
        var sourceDirectory = TestsSourceDirectory();
        var offending = Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => !PointerFileNoOverrideExemptFileNames.Contains(Path.GetFileName(path)))
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Where(file => PointerFilePattern.IsMatch(file.Text) && !PointerOverridePattern.IsMatch(file.Text))
            .Select(file => file.Path)
            .ToList();

        Assert.Empty(offending); // expected count: 0
    }

    // Tests reach the autostart registry code only through a throwaway key path; naming the real Run
    // or StartupApproved key would let a test flip the machine's actual autostart state.
    [Fact]
    public void No_test_source_file_names_the_real_autostart_registry_keys()
    {
        var sourceDirectory = TestsSourceDirectory();
        var offending = Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => Path.GetFileName(path) != "TestPathsGuardTests.cs")
            .Where(path => RealAutostartKeyPattern.IsMatch(File.ReadAllText(path)))
            .ToList();

        Assert.Empty(offending); // expected count: 0
    }

    [Fact]
    public void No_test_source_file_names_the_old_leftover_folder_naming()
    {
        var sourceDirectory = TestsSourceDirectory();
        var offending = Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => !SkippedFileNames.Contains(Path.GetFileName(path)))
            .Where(path => LeftoverFolderNamePattern.IsMatch(File.ReadAllText(path)))
            .ToList();

        Assert.Empty(offending); // expected count: 0
    }

    private static string TestsSourceDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(thisFile)!;
}
