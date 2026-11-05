using System.Windows;
using System.Windows.Media;
using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// <see cref="ProjectIconLoader"/>: SVG and Android vector / adaptive icons become frozen drawings
/// with a square viewport and a dominant color, and a file it cannot read is simply null.
/// </summary>
public class ProjectIconLoaderTests
{
    private static string Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static double HueOf(Color color) => ChartPalette.HslOf(color).H;

    private static double HueDistance(double a, double b)
    {
        var diff = Math.Abs(a - b) % 360.0;
        return diff > 180.0 ? 360.0 - diff : diff;
    }

    private static void AssertHueNear(Color expected, Color? actual)
    {
        Assert.NotNull(actual);
        Assert.True(HueDistance(HueOf(expected), HueOf(actual!.Value)) < 15.0,
            $"expected a hue near {HueOf(expected)}, got {HueOf(actual.Value)}");
    }

    private static IEnumerable<GeometryDrawing> Shapes(Drawing drawing)
    {
        if (drawing is GeometryDrawing geometry)
            yield return geometry;
        else if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var shape in Shapes(child))
                    yield return shape;
    }

    private static DrawingImage LoadVector(string path)
    {
        var image = Assert.IsType<DrawingImage>(ProjectIconLoader.Load(path, 32));
        Assert.True(image.IsFrozen);
        return image;
    }

    private const string RedSquare =
        """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><rect width="100" height="100" fill="#e02020"/></svg>""";

    [Fact]
    public void SvgBecomesAFrozenImageWithASquareViewport()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-svg");
        var wide = Write(dir.Path, "wide.svg",
            """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50"><rect width="100" height="50" fill="#2060e0"/></svg>""");

        var image = LoadVector(wide);

        Assert.Equal(image.Width, image.Height, 3);
        Assert.Equal(100, image.Width, 3);
    }

    [Fact]
    public void SvgWithoutViewBoxUsesWidthAndHeight()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-svg-size");
        var path = Write(dir.Path, "sized.svg",
            """<svg xmlns="http://www.w3.org/2000/svg" width="64" height="48"><circle cx="32" cy="24" r="20" fill="green"/></svg>""");

        var image = LoadVector(path);

        Assert.Equal(64, image.Width, 3);
        Assert.Equal(64, image.Height, 3);
    }

    [Fact]
    public void SvgDominantColorFollowsTheFillAndSkipsNoneAndHiddenShapes()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-svg-color");
        var path = Write(dir.Path, "mixed.svg",
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
              <rect width="100" height="100" fill="none"/>
              <rect width="100" height="100" style="fill:#ff0000" display="none"/>
              <rect x="10" y="10" width="80" height="80" rx="12" style="fill:#2040e0;fill-opacity:0.9"/>
              <circle cx="50" cy="50" r="6" fill="#ff0000"/>
            </svg>
            """);

        AssertHueNear(Color.FromRgb(0x20, 0x40, 0xE0), ProjectIconLoader.DominantColor(path));
    }

    [Fact]
    public void SvgGradientFillReadsAsItsFirstStop()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-svg-gradient");
        var path = Write(dir.Path, "gradient.svg",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 10 10">
              <defs>
                <linearGradient id="a"><stop offset="0" stop-color="#10c030"/><stop offset="1" stop-color="#ff0000"/></linearGradient>
                <linearGradient id="b" xlink:href="#a"/>
              </defs>
              <rect width="10" height="10" fill="url(#b)"/>
            </svg>
            """);

        AssertHueNear(Color.FromRgb(0x10, 0xC0, 0x30), ProjectIconLoader.DominantColor(path));
    }

    [Fact]
    public void SvgGroupTransformsMoveTheirShapes()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-svg-transform");
        var path = Write(dir.Path, "moved.svg",
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
              <g transform="translate(10 20) scale(2)"><rect width="10" height="10" fill="#d02020"/></g>
              <g transform="matrix(1 0 0 1 60 60)"><rect width="5" height="5" fill="#2020d0"/></g>
            </svg>
            """);

        var bounds = Shapes(LoadVector(path).Drawing).Where(shape => shape.Brush is SolidColorBrush { Color.A: > 0, Color.R: > 0x80 })
            .Select(shape => shape.Geometry.Bounds).Single();

        Assert.Equal(10, bounds.X, 3);
        Assert.Equal(20, bounds.Y, 3);
        Assert.Equal(20, bounds.Width, 3);
    }

    [Fact]
    public void SvgPolygonEllipseAndPathAreDrawn()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-svg-shapes");
        var path = Write(dir.Path, "shapes.svg",
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
              <polygon points="0,0 50,0 25,40" fill="#aa2222"/>
              <polyline points="0,60 20,80 40,60" fill="#22aa22"/>
              <ellipse cx="70" cy="70" rx="20" ry="10" fill="#2222aa"/>
              <path d="M 0 90 L 30 90 L 15 100 Z" fill="#aaaa22"/>
              <path d="M broken" fill="#ffffff"/>
            </svg>
            """);

        Assert.Equal(4, Shapes(LoadVector(path).Drawing).Count(shape => shape.Brush is SolidColorBrush { Color.A: 255 }));
    }

    [Fact]
    public void SvgScriptsTextAndExternalReferencesAreIgnored()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-svg-ignored");
        var path = Write(dir.Path, "ignored.svg",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 10 10">
              <script>alert(1)</script>
              <text x="0" y="5" fill="#ff0000">hello</text>
              <image xlink:href="file:///C:/secret.png" width="10" height="10"/>
              <use xlink:href="#missing"/>
              <rect width="10" height="10" fill="#2040e0"/>
            </svg>
            """);

        var shapes = Shapes(LoadVector(path).Drawing).Where(shape => shape.Brush is SolidColorBrush { Color.A: 255 }).ToList();

        Assert.Single(shapes);
    }

    [Fact]
    public void SvgWithAnIllustratorDoctypeLoadsButAnEntityIsNeverExpanded()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-svg-dtd");
        var secret = Write(dir.Path, "secret.txt", "TOP-SECRET");
        var plain = Write(dir.Path, "plain.svg",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect width="10" height="10" fill="#2040e0"/></svg>
            """);
        var entity = Write(dir.Path, "entity.svg",
            $"""
            <?xml version="1.0"?>
            <!DOCTYPE svg [<!ENTITY x SYSTEM "{new Uri(secret).AbsoluteUri}">]>
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><title>&x;</title><rect width="10" height="10" fill="#2040e0"/></svg>
            """);
        var bomb = Write(dir.Path, "bomb.svg",
            """
            <?xml version="1.0"?>
            <!DOCTYPE svg [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">]>
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><title>&c;</title><rect width="10" height="10" fill="#2040e0"/></svg>
            """);

        Assert.NotNull(ProjectIconLoader.Load(plain, 32));
        // A reference the reader may not resolve is an unreadable file, never the other file's content.
        Assert.Null(ProjectIconLoader.Load(entity, 32));
        Assert.Null(ProjectIconLoader.Load(bomb, 32));
    }

    [Fact]
    public void UnreadableVectorFilesGiveNullWithoutThrowing()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-bad");
        var cases = new[]
        {
            Write(dir.Path, "notxml.svg", "this is not xml"),
            Write(dir.Path, "empty.svg", ""),
            Write(dir.Path, "nothing.svg", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"/>"""),
            Write(dir.Path, "zero.svg", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect width="0" height="5"/><circle r="0"/></svg>"""),
            Write(dir.Path, "other.svg", """<html><body/></html>"""),
            Write(dir.Path, "truncated.svg", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect width="10" """),
            Path.Combine(dir.Path, "missing.svg"),
        };

        foreach (var path in cases)
        {
            Assert.Null(ProjectIconLoader.Load(path, 32));
            Assert.Null(ProjectIconLoader.DominantColor(path));
            Assert.False(ProjectIconLoader.IsRenderable(path));
        }
    }

    [Fact]
    public void AVectorFileOverTheSizeCapIsNull()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-big");
        var padding = new string(' ', (int)ProjectIconLoader.MaxFileBytes);
        var path = Write(dir.Path, "big.svg", RedSquare + padding);

        Assert.Null(ProjectIconLoader.Load(path, 32));
    }

    [Fact]
    public void ALoadedIconIsUsableOnAThreadPoolThread()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-thread");
        var svg = Write(dir.Path, "a.svg", RedSquare);
        var res = Path.Combine(dir.Path, "app", "res");
        var android = Write(dir.Path, "app/res/drawable/ic.xml", AndroidVector("#ff2040e0"));

        var (image, color, androidImage) = Task.Run(() =>
            (ProjectIconLoader.Load(svg, 32), ProjectIconLoader.DominantColor(svg), ProjectIconLoader.Load(android, 32))).GetAwaiter().GetResult();

        Assert.NotNull(image);
        Assert.True(image!.IsFrozen);
        Assert.NotNull(color);
        Assert.True(androidImage!.IsFrozen);
        Assert.True(Directory.Exists(res));
    }

    private static string AndroidVector(string fill, string extra = "") =>
        $"""
        <vector xmlns:android="http://schemas.android.com/apk/res/android"
            android:width="24dp" android:height="24dp" android:viewportWidth="24" android:viewportHeight="24">
          {extra}
          <path android:pathData="M0,0h24v24h-24z" android:fillColor="{fill}"/>
        </vector>
        """;

    [Fact]
    public void AndroidVectorReadsColorsGroupsAndAlpha()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-android");
        Write(dir.Path, "res/values/colors.xml",
            """<resources><color name="brand">#20a040</color><color name="alias">@color/brand</color></resources>""");
        var path = Write(dir.Path, "res/drawable/ic.xml",
            """
            <vector xmlns:android="http://schemas.android.com/apk/res/android"
                android:width="24dp" android:height="24dp" android:viewportWidth="24" android:viewportHeight="12">
              <group android:translateX="4" android:translateY="2" android:scaleX="0.5" android:scaleY="0.5">
                <path android:pathData="M0,0h8v8h-8z" android:fillColor="@color/alias" android:fillAlpha="0.9"/>
              </group>
              <path android:pathData="M0,0h2v2h-2z" android:fillColor="#f00" android:fillAlpha="0.1"/>
            </vector>
            """);

        var image = LoadVector(path);
        var moved = Shapes(image.Drawing).Where(shape => shape.Brush is SolidColorBrush { Color.A: > 200 }).Select(shape => shape.Geometry.Bounds).Single();

        Assert.Equal(24, image.Width, 3);
        Assert.Equal(24, image.Height, 3);
        Assert.Equal(4, moved.X, 3);
        Assert.Equal(2, moved.Y, 3);
        Assert.Equal(4, moved.Width, 3);
        AssertHueNear(Color.FromRgb(0x20, 0xA0, 0x40), ProjectIconLoader.DominantColor(path));
    }

    [Theory]
    [InlineData("#f00")]
    [InlineData("#ff0000")]
    [InlineData("#80ff0000")]
    [InlineData("#8f00")]
    public void AndroidColorFormatsAreAllRead(string fill)
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-android-formats");
        var path = Write(dir.Path, "res/drawable/ic.xml", AndroidVector(fill));

        Assert.NotNull(ProjectIconLoader.Load(path, 32));
    }

    [Fact]
    public void AndroidAdaptiveIconComposesForegroundOverBackgroundInTheSafeZone()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-adaptive");
        Write(dir.Path, "res/values/colors.xml", """<resources><color name="ic_launcher_background">#2040e0</color></resources>""");
        Write(dir.Path, "res/drawable/ic_launcher_foreground.xml",
            """
            <vector xmlns:android="http://schemas.android.com/apk/res/android"
                android:width="108dp" android:height="108dp" android:viewportWidth="108" android:viewportHeight="108">
              <path android:pathData="M30,30h48v48h-48z" android:fillColor="#ffffff"/>
            </vector>
            """);
        var adaptive = Write(dir.Path, "res/mipmap-anydpi-v26/ic_launcher.xml",
            """
            <adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
              <background android:drawable="@color/ic_launcher_background"/>
              <foreground android:drawable="@drawable/ic_launcher_foreground"/>
            </adaptive-icon>
            """);

        var image = LoadVector(adaptive);

        Assert.Equal(72, image.Width, 3);
        Assert.Equal(72, image.Height, 3);
        // White on blue: the background decides the color.
        AssertHueNear(Color.FromRgb(0x20, 0x40, 0xE0), ProjectIconLoader.DominantColor(adaptive));
    }

    [Fact]
    public void AndroidAdaptiveIconWithAPngLayerAndAnInsetIsRead()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-adaptive-png");
        var pixels = new byte[16 * 16 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 1] = 200;
            pixels[i + 3] = 255;
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 64);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.Combine(dir.Path, "res", "mipmap-xxhdpi"));
        using (var stream = File.Create(Path.Combine(dir.Path, "res", "mipmap-xxhdpi", "fg.png")))
            encoder.Save(stream);
        Write(dir.Path, "res/drawable/ic_bg.xml", AndroidVector("#ff202020"));
        Write(dir.Path, "res/drawable/fg_inset.xml",
            """<inset xmlns:android="http://schemas.android.com/apk/res/android" android:drawable="@mipmap/fg" android:inset="16dp"/>""");
        var adaptive = Write(dir.Path, "res/mipmap-anydpi/ic.xml",
            """
            <adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
              <background android:drawable="@drawable/ic_bg"/>
              <foreground><inset android:drawable="@mipmap/fg" android:inset="16dp"/></foreground>
            </adaptive-icon>
            """);

        LoadVector(adaptive);
        AssertHueNear(Color.FromRgb(0, 200, 0), ProjectIconLoader.DominantColor(adaptive));
    }

    [Fact]
    public void AnAdaptiveIconWithoutAReadableForegroundIsNull()
    {
        using var dir = TestPaths.CreateDisposableDirectory("icon-adaptive-bad");
        var adaptive = Write(dir.Path, "res/mipmap-anydpi-v26/ic.xml",
            """
            <adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
              <foreground android:drawable="@drawable/nope"/>
            </adaptive-icon>
            """);

        Assert.Null(ProjectIconLoader.Load(adaptive, 32));
    }

    [Theory]
    [InlineData("a/res/drawable/ic.xml", true)]
    [InlineData("a/res/mipmap-anydpi-v26/ic.xml", true)]
    [InlineData("a/res/drawable-xxhdpi/ic.xml", true)]
    [InlineData("a/res/values/colors.xml", false)]
    [InlineData("a/pom.xml", false)]
    [InlineData("a/config/ic.xml", false)]
    public void AnXmlFileCountsAsAnIconOnlyInsideAnAndroidResFolder(string relative, bool expected)
    {
        var path = Path.Combine(TestPaths.Root, relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected, ProjectIconLoader.IsAndroidResourceFile(path));
    }

    [Theory]
    [InlineData("icon.ico", true)]
    [InlineData("icon.PNG", true)]
    [InlineData("icon.svg", true)]
    [InlineData("icon.xml", true)]
    [InlineData("icon.jpg", false)]
    [InlineData("icon", false)]
    public void IconExtensionsIncludeSvgAndXml(string name, bool expected) =>
        Assert.Equal(expected, ProjectIconLoader.HasIconExtension(name));
}
