using System.Runtime.CompilerServices;

namespace AiUsage.Tests;

/// <summary>
/// <c>app.ico</c> must contain every size Windows actually asks for
/// (taskbar, Alt-Tab, Explorer's large-icon view, the installer). Reads the ICO container's own
/// directory header directly (a plain binary format, ICONDIR + ICONDIRENTRY) instead of adding an
/// image-decoding dependency just for one structural check.
/// </summary>
public class AppIconTests
{
    private static readonly string AssetsDir = FindAssetsDir();

    private static string FindAssetsDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(Path.GetDirectoryName(here))!;
        var repoRoot = Path.GetDirectoryName(testsDir)!;
        return Path.Combine(repoRoot, "src", "AiUsage", "Assets");
    }

    [Fact]
    public void AppIcon_contains_every_shell_size_including_the_fractional_scaling_steps()
    {
        var sizes = ReadIconSizes(Path.Combine(AssetsDir, "app.ico"));

        Assert.Equal([16, 20, 24, 32, 40, 48, 64, 96, 128, 256], sizes.OrderBy(s => s));
    }

    // The in-app mark is only ever drawn downsampled; a smaller source would be upscaled and blur.
    [Fact]
    public void AppMark_png_is_256_px_square()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AssetsDir, "app-mark.png"));
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));

        Assert.Equal((256, 256), (width, height));
    }

    [Theory]
    [InlineData("tray-ok.ico")]
    [InlineData("tray-warn.ico")]
    [InlineData("tray-crit.ico")]
    public void TrayIcon_contains_a_16px_frame_for_the_taskbar(string fileName)
    {
        var sizes = ReadIconSizes(Path.Combine(AssetsDir, fileName));

        Assert.Contains(16, sizes);
    }

    /// <summary>
    /// Parses just the ICONDIR/ICONDIRENTRY header (the first 6 + 16*N bytes of any .ico file) to
    /// list the width of every embedded frame - 0 in that single byte means 256 per the format spec.
    /// </summary>
    private static List<int> ReadIconSizes(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        reader.ReadUInt16(); // reserved, always 0
        var imageType = reader.ReadUInt16();
        Assert.Equal(1, imageType); // 1 = icon, 2 = cursor
        var count = reader.ReadUInt16();

        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var width = reader.ReadByte();
            sizes.Add(width == 0 ? 256 : width);
            reader.ReadBytes(15); // rest of ICONDIRENTRY: height, colours, reserved, planes, bitcount, size, offset
        }

        return sizes;
    }
}
