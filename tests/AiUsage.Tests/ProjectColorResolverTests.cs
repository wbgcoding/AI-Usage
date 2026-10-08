using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// <see cref="ProjectColorResolver"/>'s own three color sources (icon, color word, stable-hash
/// fallback) and its JSON cache, against fixture folders under <see cref="TestPaths.Root"/>.
/// </summary>
public class ProjectColorResolverTests
{
    private static void WriteSolidColorPng(string path, Color color, int size = 8)
    {
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static double HueDistance(double a, double b)
    {
        var diff = Math.Abs(a - b) % 360.0;
        return diff > 180.0 ? 360.0 - diff : diff;
    }

    [Fact]
    public void DominantIconColorReadsTheClearDominantHueOfASolidColorIcon()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon");
        var iconPath = Path.Combine(dir.Path, "icon.png");
        WriteSolidColorPng(iconPath, Colors.Red);

        var resolved = ProjectColorResolver.DominantIconColor(iconPath);

        Assert.NotNull(resolved);
        var expectedHue = ChartPalette.HslOf(Colors.Red).H;
        var actualHue = ChartPalette.HslOf(resolved!.Value).H;
        Assert.True(HueDistance(expectedHue, actualHue) < 10.0,
            $"expected a hue near {expectedHue}, got {actualHue}");
    }

    [Fact]
    public void DominantIconColorIsNullWithNoQualifyingPixel()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-gray");
        var iconPath = Path.Combine(dir.Path, "icon.png");
        // Pure gray: zero saturation, so every pixel is filtered out before the histogram.
        WriteSolidColorPng(iconPath, Color.FromRgb(128, 128, 128));

        Assert.Null(ProjectColorResolver.DominantIconColor(iconPath));
    }

    [Theory]
    [InlineData("red-tool")]
    [InlineData("Tool-Red")]
    [InlineData("my.red.thing")]
    public void ColorWordHueMatchesAWholeNameSegment(string projectName)
    {
        var hue = ProjectColorResolver.ColorWordHue(projectName);

        Assert.NotNull(hue);
        Assert.Equal(ChartPalette.HslOf(Colors.Red).H, hue!.Value, precision: 6);
    }

    [Fact]
    public void ColorWordHueIgnoresAColorWordInsideALargerSegment()
    {
        // "red" appears inside "credential" but is not its own name segment.
        Assert.Null(ProjectColorResolver.ColorWordHue("credential-manager"));
    }

    [Fact]
    public void FindIconFileHonoursThePriorityOrderOverAPlainIco()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-priority");
        File.WriteAllBytes(Path.Combine(dir.Path, "random.ico"), [0]);
        var favicon = Path.Combine(dir.Path, "favicon.ico");
        File.WriteAllBytes(favicon, [0]);

        Assert.Equal(favicon, ProjectColorResolver.FindIconFile(dir.Path));
    }

    [Fact]
    public void FindIconFileSkipsBuildAndDependencyFolders()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-skip");
        var nodeModules = Path.Combine(dir.Path, "node_modules");
        Directory.CreateDirectory(nodeModules);
        File.WriteAllBytes(Path.Combine(nodeModules, "favicon.ico"), [0]);

        Assert.Null(ProjectColorResolver.FindIconFile(dir.Path));
    }

    private static string Touch(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static void WriteText(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void FindIconFileUsesTheApplicationIconOfACsprojBeforeAnyNamedFile()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-csproj");
        Touch(dir.Path, "design/logo.png");
        var icon = Touch(dir.Path, "src/Tool/Assets/app.ico");
        var declared = Touch(dir.Path, "src/Tool/Resources/network.ico");
        WriteText(dir.Path, "src/Tool/Tool.csproj", "<Project><PropertyGroup><ApplicationIcon>Resources\\network.ico</ApplicationIcon></PropertyGroup></Project>");

        Assert.NotEqual(icon, declared);
        Assert.Equal(declared, ProjectColorResolver.FindIconFile(dir.Path));
    }

    [Fact]
    public void ADeclaredIconOutsideTheProjectRootIsNeverPicked()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-escape");
        var project = Path.Combine(dir.Path, "proj");
        var outside = Touch(dir.Path, "outside.ico");
        WriteText(project, "Tool.csproj", "<Project><PropertyGroup><ApplicationIcon>..\\outside.ico</ApplicationIcon></PropertyGroup></Project>");

        Assert.NotEqual(outside, ProjectColorResolver.FindIconFile(project));

        var absolute = Touch(dir.Path, "elsewhere/abs.ico");
        WriteText(project, "Tool.csproj", $"<Project><PropertyGroup><ApplicationIcon>{absolute}</ApplicationIcon></PropertyGroup></Project>");

        Assert.NotEqual(absolute, ProjectColorResolver.FindIconFile(project));
    }

    [Theory]
    [InlineData(@"..\..\x.ico")]
    [InlineData(@"C:\x.ico")]
    [InlineData(@"\server\share\x.ico")]
    [InlineData("//server/share/x.ico")]
    [InlineData(@"\x.ico")]
    public void CombineRelativeRejectsEscapingRootedAndUncPaths(string relative)
    {
        var root = Path.Combine(TestPaths.Root, "combine-root");

        Assert.Null(ProjectColorResolver.CombineRelative(root, Path.Combine(root, "src"), relative));
    }

    [Fact]
    public void CombineRelativeKeepsAPathInsideTheRootAndRejectsASiblingWithTheSamePrefix()
    {
        var root = Path.Combine(TestPaths.Root, "combine-root");

        Assert.Equal(Path.Combine(root, "src", "a.ico"), ProjectColorResolver.CombineRelative(root, Path.Combine(root, "src"), "a.ico"));
        Assert.Null(ProjectColorResolver.CombineRelative(root, root, @"..\combine-root-other\a.ico"));
    }

    [Fact]
    public void AJunctionInsideTheProjectIsNotWalked()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-junction");
        var project = Path.Combine(dir.Path, "proj");
        Directory.CreateDirectory(project);
        Touch(dir.Path, "target/icon.ico");
        var link = Path.Combine(project, "link");

        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(dir.Path, "target")}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using (var process = System.Diagnostics.Process.Start(start)!)
            process.WaitForExit();
        if (!Directory.Exists(link))
            return; // The OS refused to create the junction; nothing to prove here.

        try
        {
            Assert.Null(ProjectColorResolver.FindIconFile(project));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void ADeclaredIconBehindALinkedFolderOrALinkedFileIsRefused()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-declared-link");
        var project = Path.Combine(dir.Path, "proj");
        Directory.CreateDirectory(project);
        var outside = Touch(dir.Path, "target/icon.ico");
        var folderLink = Path.Combine(project, "linked");
        var fileLink = Path.Combine(project, "file.ico");

        if (!TryMakeLink($"/J \"{folderLink}\" \"{Path.Combine(dir.Path, "target")}\""))
            return; // The OS refused to create the junction; nothing to prove here.

        try
        {
            Assert.False(ProjectColorResolver.IsUsableIconFile(project, Path.Combine(folderLink, "icon.ico")));

            if (TryMakeLink($"\"{fileLink}\" \"{outside}\"") && File.Exists(fileLink))
                Assert.False(ProjectColorResolver.IsUsableIconFile(project, fileLink));
        }
        finally
        {
            Directory.Delete(folderLink);
            if (File.Exists(fileLink))
                File.Delete(fileLink);
        }
    }

    [Fact]
    public void IsUsableIconFileKeepsAPlainFileInsideTheRootAndRefusesOneOutside()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-gate");
        var project = Path.Combine(dir.Path, "proj");
        var inside = Touch(project, "res/icon.ico");
        var outside = Touch(dir.Path, "other/icon.ico");

        Assert.True(ProjectColorResolver.IsUsableIconFile(project, inside));
        Assert.False(ProjectColorResolver.IsUsableIconFile(project, outside));
        Assert.False(ProjectColorResolver.IsUsableIconFile(project, @"\attacker\share\x.png"));
    }

    private static bool TryMakeLink(string mklinkArguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink " + mklinkArguments)
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
        return process.ExitCode == 0;
    }

    [Fact]
    public void AHugeImageIsNotDecodedForItsColor()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-huge");
        var path = Path.Combine(dir.Path, "huge.png");
        // A 1-bit 20000 x 20000 PNG: tiny on disk, 400 million pixels once decoded.
        var bitmap = BitmapSource.Create(20000, 20000, 96, 96, PixelFormats.BlackWhite, null, new byte[2500 * 20000], 2500);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(ProjectColorResolver.DominantIconColor(path));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "decoding must be refused, not performed");
    }

    [Fact]
    public void FindIconFileUsesTheFirstBundleIconOfATauriConfig()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-tauri");
        Touch(dir.Path, "src-tauri/icons/32x32.svg");
        var icon = Touch(dir.Path, "src-tauri/icons/icon.ico");
        WriteText(dir.Path, "src-tauri/tauri.conf.json", "{\"bundle\":{\"icon\":[\"icons/missing.png\",\"icons/icon.ico\"]}}");

        Assert.Equal(icon, ProjectColorResolver.FindIconFile(dir.Path));
    }

    [Fact]
    public void FindIconFileReadsTheLauncherIconNamedByAnAndroidManifest()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-android");
        WriteText(dir.Path, "app/src/main/AndroidManifest.xml", "<manifest><application android:icon=\"@mipmap/ic_launcher\" /></manifest>");
        Touch(dir.Path, "app/src/main/res/mipmap-mdpi/ic_launcher.png");
        var best = Touch(dir.Path, "app/src/main/res/mipmap-xxxhdpi/ic_launcher.png");
        Touch(dir.Path, "app/src/main/res/mipmap-xhdpi/ic_launcher.png");

        Assert.Equal(best, ProjectColorResolver.FindIconFile(dir.Path));
    }

    [Fact]
    public void AnAndroidManifestIconNameCannotLeaveTheProject()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-android-escape");
        var project = Path.Combine(dir.Path, "proj");
        Touch(dir.Path, "evil.png");
        WriteText(project, "app/src/main/AndroidManifest.xml", AndroidManifest(@"..\..\..\..\..\..\evil"));

        Assert.Null(ProjectColorResolver.FindIconFile(project));
    }

    [Fact]
    public void AnAndroidManifestIconNameWithAUncPathGivesNoCandidate()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-android-unc");
        WriteText(dir.Path, "app/src/main/AndroidManifest.xml", AndroidManifest(@"\attacker\share\x"));

        Assert.Null(ProjectColorResolver.FindIconFile(dir.Path));
    }

    private static string AndroidManifest(string iconName) =>
        "<manifest><application android:icon=\"@mipmap/" + iconName + "\" /></manifest>";

    [Fact]
    public void FindIconFileReadsExpoAndGodotIcons()
    {
        using var expo = TestPaths.CreateDisposableDirectory("project-color-icon-expo");
        var expoIcon = Touch(expo.Path, "assets/icon.png");
        WriteText(expo.Path, "app.json", "{\"expo\":{\"icon\":\"./assets/icon.png\"}}");
        Assert.Equal(expoIcon, ProjectColorResolver.FindIconFile(expo.Path));

        using var godot = TestPaths.CreateDisposableDirectory("project-color-icon-godot");
        var godotIcon = Touch(godot.Path, "art/mark.png");
        WriteText(godot.Path, "project.godot", "[application]\nconfig/icon=\"res://art/mark.png\"\n");
        Assert.Equal(godotIcon, ProjectColorResolver.FindIconFile(godot.Path));
    }

    [Fact]
    public void FindIconFileFindsAnIconBehindManyFilesOnTheFirstLevel()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-many");
        for (var i = 0; i < 300; i++)
            Touch(dir.Path, $"file{i:000}.txt");
        var icon = Touch(dir.Path, "zzz/app.ico");

        Assert.Equal(icon, ProjectColorResolver.FindIconFile(dir.Path));
    }

    [Fact]
    public void FindIconFileNeverPicksASplashIconOverAnotherIco()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-splash");
        Touch(dir.Path, "splash.ico");
        var random = Touch(dir.Path, "random.ico");

        Assert.Equal(random, ProjectColorResolver.FindIconFile(dir.Path));
    }

    [Fact]
    public void FindIconFileRanksTheProjectNamedIcoAfterAppIcoAndSkipsHiddenFolders()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-named");
        var projectName = Path.GetFileName(dir.Path);
        Touch(dir.Path, ".hidden/app.ico");
        Touch(dir.Path, "icon.ico");
        var named = Touch(dir.Path, projectName + ".ico");

        Assert.Equal(named, ProjectColorResolver.FindIconFile(dir.Path));
    }

    [Fact]
    public void ResolveAllGivesTwoPlainNamesDistinctStableColors()
    {
        using var alpha = TestPaths.CreateDisposableDirectory("alpha-plain-project");
        using var bravo = TestPaths.CreateDisposableDirectory("bravo-plain-project");
        var cachePath = TestPaths.GetPath("project-colors-distinct", ".json");

        var first = ProjectColorResolver.ResolveAll([alpha.Path, bravo.Path], cachePath);
        var second = ProjectColorResolver.ResolveAll([alpha.Path, bravo.Path], cachePath);

        Assert.NotEqual(first[alpha.Path].Color, first[bravo.Path].Color);
        // Stable across a second call (whether served from cache or recomputed identically).
        Assert.Equal(first[alpha.Path].Color, second[alpha.Path].Color);
        Assert.Equal(first[bravo.Path].Color, second[bravo.Path].Color);
    }

    [Fact]
    public void ResolveAllCacheRoundTripsAndRejectsAnUnrecognisedVersion()
    {
        using var project = TestPaths.CreateDisposableDirectory("gamma-cache-project");
        var cachePath = TestPaths.GetPath("project-colors-roundtrip", ".json");

        var first = ProjectColorResolver.ResolveAll([project.Path], cachePath);
        var originalColor = first[project.Path].Color;

        Assert.True(File.Exists(cachePath));
        Assert.Contains(project.Path.Replace("\\", "\\\\"), File.ReadAllText(cachePath));

        // A cache file from a version this build does not recognise must never be trusted -
        // overwrite it with a bogus entry under an unrecognised version number.
        var escapedPath = project.Path.Replace("\\", "\\\\");
        File.WriteAllText(cachePath,
            "{\"Version\":99,\"Entries\":{\"" + escapedPath +
            "\":{\"IconPath\":null,\"IconLastWriteUtc\":null,\"ColorHex\":\"#000000\"}}}");

        var afterVersionReject = ProjectColorResolver.ResolveAll([project.Path], cachePath);

        Assert.Equal(originalColor, afterVersionReject[project.Path].Color);
    }
}
