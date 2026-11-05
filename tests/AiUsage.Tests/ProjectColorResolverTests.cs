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

    [Theory]
    [InlineData("reports")]
    [InlineData("coverage")]
    [InlineData("playwright-report")]
    [InlineData("test-results")]
    [InlineData("graphify-out")]
    [InlineData("out")]
    [InlineData("target")]
    [InlineData("e2e")]
    public void FindIconFileSkipsGeneratedOutputFolders(string folder)
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-generated");
        Touch(dir.Path, folder + "/css/logo.png");

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

    private const string BlueSvg =
        """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect width="10" height="10" fill="#2040e0"/></svg>""";

    [Fact]
    public void IsUsableIconFileAcceptsADrawingSvgAndAnAndroidResXmlButNoOtherXml()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-vector");
        var project = dir.Path;
        WriteText(project, "art/logo.svg", BlueSvg);
        WriteText(project, "art/blank.svg", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"/>""");
        WriteText(project, "app/res/drawable/ic.xml", BlueSvg);
        WriteText(project, "config/ic.xml", BlueSvg);

        Assert.True(ProjectColorResolver.IsUsableIconFile(project, Path.Combine(project, "art", "logo.svg")));
        Assert.False(ProjectColorResolver.IsUsableIconFile(project, Path.Combine(project, "art", "blank.svg")));
        Assert.True(ProjectColorResolver.IsUsableIconFile(project, Path.Combine(project, "app", "res", "drawable", "ic.xml")));
        Assert.False(ProjectColorResolver.IsUsableIconFile(project, Path.Combine(project, "config", "ic.xml")));
    }

    [Fact]
    public void DominantIconColorReadsASvgIcon()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-svg");
        WriteText(dir.Path, "icon.svg", BlueSvg);

        var resolved = ProjectColorResolver.DominantIconColor(Path.Combine(dir.Path, "icon.svg"));

        Assert.NotNull(resolved);
        Assert.True(HueDistance(ChartPalette.HslOf(Color.FromRgb(0x20, 0x40, 0xE0)).H, ChartPalette.HslOf(resolved!.Value).H) < 15.0);
    }

    private const string AdaptiveXml =
        """
        <adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
          <background android:drawable="@color/ic_launcher_background"/>
          <foreground android:drawable="@drawable/ic_launcher_foreground"/>
        </adaptive-icon>
        """;

    private static void WriteAdaptiveLayers(string root, string mainRelative)
    {
        WriteText(root, mainRelative + "/res/values/colors.xml", """<resources><color name="ic_launcher_background">#2040e0</color></resources>""");
        WriteText(root, mainRelative + "/res/drawable/ic_launcher_foreground.xml",
            """
            <vector xmlns:android="http://schemas.android.com/apk/res/android" android:viewportWidth="108" android:viewportHeight="108">
              <path android:pathData="M30,30h48v48h-48z" android:fillColor="#fff"/>
            </vector>
            """);
    }

    [Theory]
    [InlineData("mipmap-anydpi-v26")]
    [InlineData("mipmap-anydpi")]
    public void FindIconFileTakesTheAdaptiveIconWhenNoDensityPngExists(string folder)
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-adaptive");
        WriteText(dir.Path, "app/src/main/AndroidManifest.xml", AndroidManifest("ic_launcher"));
        WriteAdaptiveLayers(dir.Path, "app/src/main");
        var adaptive = Path.Combine(dir.Path, "app", "src", "main", "res", folder, "ic_launcher.xml");
        WriteText(dir.Path, $"app/src/main/res/{folder}/ic_launcher.xml", AdaptiveXml);

        Assert.Equal(adaptive, ProjectColorResolver.FindIconFile(dir.Path));
    }

    [Fact]
    public void FindIconFilePrefersADensityPngOverTheAdaptiveIcon()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-adaptive-png");
        WriteText(dir.Path, "app/src/main/AndroidManifest.xml", AndroidManifest("ic_launcher"));
        WriteAdaptiveLayers(dir.Path, "app/src/main");
        WriteText(dir.Path, "app/src/main/res/mipmap-anydpi-v26/ic_launcher.xml", AdaptiveXml);
        var png = Touch(dir.Path, "app/src/main/res/mipmap-xhdpi/ic_launcher.png");

        Assert.Equal(png, ProjectColorResolver.FindIconFile(dir.Path));
    }

    [Fact]
    public void AnAdaptiveIconWhoseLayersCannotBeReadIsNoIcon()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-adaptive-broken");
        WriteText(dir.Path, "app/src/main/AndroidManifest.xml", AndroidManifest("ic_launcher"));
        WriteText(dir.Path, "app/src/main/res/mipmap-anydpi-v26/ic_launcher.xml", AdaptiveXml);

        Assert.Null(ProjectColorResolver.FindIconFile(dir.Path));
    }

    [Fact]
    public void FindIconFileReadsASvgNamedByAGodotProjectAndByATauriConfig()
    {
        using var godot = TestPaths.CreateDisposableDirectory("project-color-icon-godot-svg");
        WriteText(godot.Path, "icon.svg", BlueSvg);
        WriteText(godot.Path, "project.godot", "[application]\nconfig/icon=\"res://icon.svg\"\n");
        Assert.Equal(Path.Combine(godot.Path, "icon.svg"), ProjectColorResolver.FindIconFile(godot.Path));

        using var tauri = TestPaths.CreateDisposableDirectory("project-color-icon-tauri-svg");
        WriteText(tauri.Path, "src-tauri/icons/app.svg", BlueSvg);
        WriteText(tauri.Path, "src-tauri/tauri.conf.json", "{\"bundle\":{\"icon\":[\"icons/app.svg\",\"icons/icon.ico\"]}}");
        Assert.Equal(Path.Combine(tauri.Path, "src-tauri", "icons", "app.svg"), ProjectColorResolver.FindIconFile(tauri.Path));
    }

    // Each row lists the files of a folder best first; the first one must win over every later one.
    [Theory]
    [InlineData("icon.png", "icon.svg")]
    [InlineData("icon.svg", "logo.png")]
    [InlineData("favicon.svg", "icon.png")]
    [InlineData("logo.png", "logo.svg")]
    [InlineData("icon.png", "icon-512.png")]
    [InlineData("icon.png", "app_icon.png")]
    [InlineData("icon-128.png", "app_icon.png")]
    [InlineData("app_icon.png", "app-icon.png")]
    [InlineData("app-icon.png", "meta_app_icon.png")]
    [InlineData("meta_app_icon.png", "ic_launcher.png")]
    [InlineData("icon-512.png", "icon-192.png")]
    [InlineData("icon_512.png", "icon_96.png")]
    public void FindIconFileRanksTheWellKnownIconNames(string better, string worse)
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-rank");
        foreach (var name in new[] { worse, better })
        {
            if (name.EndsWith(".svg", StringComparison.Ordinal))
                WriteText(dir.Path, "art/" + name, BlueSvg);
            else
                Touch(dir.Path, "art/" + name);
        }

        Assert.Equal(Path.Combine(dir.Path, "art", better), ProjectColorResolver.FindIconFile(dir.Path));
    }

    [Theory]
    [InlineData("icon-48.png")]
    [InlineData("logo.svg")]
    [InlineData("favicon.svg")]
    [InlineData("meta_app_icon.png")]
    public void FindIconFileFindsAnIconUnderOneOfTheNewNames(string name)
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-names");
        if (name.EndsWith(".svg", StringComparison.Ordinal))
            WriteText(dir.Path, "public/" + name, BlueSvg);
        else
            Touch(dir.Path, "public/" + name);

        Assert.Equal(Path.Combine(dir.Path, "public", name), ProjectColorResolver.FindIconFile(dir.Path));
    }

    [Theory]
    [InlineData("icon-foreground.png")]
    [InlineData("icon-background.png")]
    [InlineData("icon-abc.png")]
    [InlineData("my-icon-48.png")]
    public void FindIconFileStillIgnoresLayerAndUnrelatedNames(string name)
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-icon-ignored");
        Touch(dir.Path, "art/" + name);

        Assert.Null(ProjectColorResolver.FindIconFile(dir.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFolderInsideACheckoutBorrowsTheIconOfTheCheckoutRoot(bool gitIsAFile)
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-inherit");
        MakeCheckout(dir.Path, gitIsAFile);
        Touch(dir.Path, "icon.png");
        Directory.CreateDirectory(Path.Combine(dir.Path, "apps", "web"));

        Assert.Equal(Path.Combine(dir.Path, "icon.png"), ProjectColorResolver.FindIconFile(Path.Combine(dir.Path, "apps", "web")));
    }

    [Fact]
    public void AFolderWithItsOwnIconKeepsItInsideACheckout()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-inherit-own");
        MakeCheckout(dir.Path, false);
        Touch(dir.Path, "icon.png");
        Touch(dir.Path, "apps/web/icon.png");

        Assert.Equal(Path.Combine(dir.Path, "apps", "web", "icon.png"), ProjectColorResolver.FindIconFile(Path.Combine(dir.Path, "apps", "web")));
    }

    [Fact]
    public void ACheckoutRootNeverBorrowsFromAnEnclosingCheckout()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-inherit-root");
        MakeCheckout(dir.Path, false);
        Touch(dir.Path, "icon.png");
        var inner = Path.Combine(dir.Path, "vendor-copy");
        MakeCheckout(inner, false);

        Assert.Null(ProjectColorResolver.FindIconFile(inner));
    }

    [Fact]
    public void AFolderOutsideAnyCheckoutBorrowsNothing()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-inherit-none");
        Touch(dir.Path, "icon.png");
        Directory.CreateDirectory(Path.Combine(dir.Path, "apps", "web"));

        Assert.Null(ProjectColorResolver.FindIconFile(Path.Combine(dir.Path, "apps", "web")));
    }

    [Fact]
    public void ACheckoutWithoutAnIconIsSkippedForTheNextOneUp()
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-inherit-skip");
        MakeCheckout(dir.Path, false);
        Touch(dir.Path, "icon.png");
        MakeCheckout(Path.Combine(dir.Path, "group"), false);
        Directory.CreateDirectory(Path.Combine(dir.Path, "group", "tool"));

        Assert.Equal(Path.Combine(dir.Path, "icon.png"), ProjectColorResolver.FindIconFile(Path.Combine(dir.Path, "group", "tool")));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void TheIconIsBorrowedFromAtMostFourLevelsUp(int levelsBelowTheCheckout, bool borrows)
    {
        using var dir = TestPaths.CreateDisposableDirectory("project-color-inherit-depth");
        MakeCheckout(dir.Path, false);
        Touch(dir.Path, "icon.png");
        var project = dir.Path;
        for (var level = 0; level < levelsBelowTheCheckout; level++)
            project = Path.Combine(project, "d" + level);
        Directory.CreateDirectory(project);

        Assert.Equal(borrows ? Path.Combine(dir.Path, "icon.png") : null, ProjectColorResolver.FindIconFile(project));
    }

    private static void MakeCheckout(string folder, bool gitIsAFile)
    {
        Directory.CreateDirectory(folder);
        var git = Path.Combine(folder, ".git");
        if (gitIsAFile)
            File.WriteAllText(git, "gitdir: elsewhere");
        else
            Directory.CreateDirectory(git);
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
    public void AProjectKeepsItsColorAcrossCallsWithDifferentFolderSets()
    {
        using var a = TestPaths.CreateDisposableDirectory("keep-a-project");
        using var b = TestPaths.CreateDisposableDirectory("keep-b-project");
        using var c = TestPaths.CreateDisposableDirectory("keep-c-project");
        using var d = TestPaths.CreateDisposableDirectory("keep-d-project");
        var cachePath = TestPaths.GetPath("project-colors-keep", ".json");

        var first = ProjectColorResolver.ResolveAll([a.Path, b.Path, c.Path], cachePath);
        ProjectColorResolver.ResolveAll([d.Path], cachePath);

        var stored = File.ReadAllText(cachePath);
        foreach (var folder in new[] { a.Path, b.Path, c.Path, d.Path })
            Assert.Contains(folder.Replace("\\", "\\\\"), stored);

        var again = ProjectColorResolver.ResolveAll([c.Path, b.Path, a.Path], cachePath);
        foreach (var folder in new[] { a.Path, b.Path, c.Path })
            Assert.Equal(first[folder].Color, again[folder].Color);
    }

    [Fact]
    public void ACacheEntryOfAFolderThatIsGoneIsDropped()
    {
        using var kept = TestPaths.CreateDisposableDirectory("drop-kept-project");
        var gone = Path.Combine(TestPaths.CreateDirectory("drop-gone-parent"), "vanished-project");
        Directory.CreateDirectory(gone);
        var cachePath = TestPaths.GetPath("project-colors-drop", ".json");
        ProjectColorResolver.ResolveAll([kept.Path, gone], cachePath);
        Directory.Delete(gone);

        ProjectColorResolver.ResolveAll([kept.Path], cachePath);

        Assert.DoesNotContain("vanished-project", File.ReadAllText(cachePath));
    }

    [Fact]
    public void AnIconThatMakesTheDecoderThrowOnlyCostsItsOwnFolderItsColor()
    {
        using var broken = TestPaths.CreateDisposableDirectory("throwing-icon-project");
        using var healthy = TestPaths.CreateDisposableDirectory("healthy-icon-project");
        var cachePath = TestPaths.GetPath("project-colors-throwing", ".json");
        var green = Color.FromRgb(20, 180, 60);

        var result = ProjectColorResolver.ResolveAll(
            [broken.Path, healthy.Path], cachePath,
            folder => Path.Combine(folder, "icon.png"),
            icon => icon.Contains("throwing-icon", StringComparison.Ordinal)
                ? throw new InvalidOperationException("damaged")
                : green);

        Assert.Equal(2, result.Count);
        Assert.Null(result[broken.Path].IconPath);
        Assert.Equal(green, result[healthy.Path].Color);
        Assert.NotNull(result[healthy.Path].IconPath);
    }

    [Fact]
    public void ANetworkFolderIsNeverWalkedAndGetsAHashColorWithoutAnIcon()
    {
        var calls = 0;
        var cachePath = TestPaths.GetPath("project-colors-unc", ".json");

        var result = ProjectColorResolver.ResolveAll(
            [@"\\offline-host\share\project"], cachePath,
            _ =>
            {
                calls++;
                return null;
            },
            _ => null);

        Assert.Equal(0, calls);
        Assert.Null(result[@"\\offline-host\share\project"].IconPath);
    }

    [Theory]
    [InlineData(@"\\host\share\project", false)]
    [InlineData(@"relative\project", false)]
    [InlineData("project", false)]
    [InlineData(@"C:\Windows", true)]
    public void OnlyAFullyQualifiedPathOnALocalDriveCountsAsLocal(string folder, bool expected) =>
        Assert.Equal(expected, ProjectColorResolver.IsLocalFolder(folder));

    [Fact]
    public void TheFolderWalkIsSkippedWhileTheFolderIsUnchanged()
    {
        using var project = TestPaths.CreateDisposableDirectory("walk-skip-project");
        var cachePath = TestPaths.GetPath("project-colors-walk", ".json");
        var calls = 0;
        string? Find(string folder)
        {
            calls++;
            return null;
        }

        ProjectColorResolver.ResolveAll([project.Path], cachePath, Find, _ => null);
        Assert.Equal(1, calls);

        ProjectColorResolver.ResolveAll([project.Path], cachePath, Find, _ => null);
        Assert.Equal(1, calls); // unchanged: served from the cache

        Directory.SetLastWriteTimeUtc(project.Path, DateTime.UtcNow.AddMinutes(5));
        ProjectColorResolver.ResolveAll([project.Path], cachePath, Find, _ => null);
        Assert.Equal(2, calls);
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
