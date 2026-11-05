using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AiUsage.Stats;

/// <summary>
/// Turns a project's icon file into something the statistics window can draw and read a dominant
/// color from: .ico/.png as a frozen bitmap, .svg and Android vector or adaptive-icon .xml as a
/// frozen <see cref="DrawingImage"/>. Everything it builds is a Freezable (never a Visual or a
/// render target), so it is safe on a thread-pool thread as well as on the UI thread. A file it
/// cannot make sense of gives null, never an exception.
/// </summary>
internal static partial class ProjectIconLoader
{
    internal const long MaxFileBytes = 2 * 1024 * 1024;
    private const long MaxPixels = 4096L * 4096L;

    internal static bool IsBitmapFile(string path) =>
        HasExtension(path, ".ico") || HasExtension(path, ".png");

    internal static bool IsVectorFile(string path) =>
        HasExtension(path, ".svg") || HasExtension(path, ".xml");

    internal static bool HasIconExtension(string path) => IsBitmapFile(path) || IsVectorFile(path);

    /// <summary>True for a file inside an Android <c>res/drawable*</c> or <c>res/mipmap*</c> folder:
    /// the only place an .xml counts as an icon.</summary>
    internal static bool IsAndroidResourceFile(string path)
    {
        var folder = Path.GetDirectoryName(path);
        var name = Path.GetFileName(folder) ?? "";
        var parent = Path.GetFileName(Path.GetDirectoryName(folder)) ?? "";
        return parent.Equals("res", StringComparison.OrdinalIgnoreCase)
            && (name.StartsWith("drawable", StringComparison.OrdinalIgnoreCase) || name.StartsWith("mipmap", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasExtension(string path, string extension) =>
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The icon as a frozen image: a bitmap decoded to <paramref name="pixelWidth"/>, or a
    /// vector drawing that scales to any size. Null when the file cannot be read as an icon.</summary>
    internal static ImageSource? Load(string path, int pixelWidth)
    {
        try
        {
            return IsVectorFile(path) ? LoadVector(path)?.ToImage() : LoadBitmap(path, pixelWidth);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>True when the file gives an image: always for a bitmap (its decoder decides later),
    /// after a parse for a vector file, so a vector file that draws nothing is never picked.</summary>
    internal static bool IsRenderable(string path) => !IsVectorFile(path) || Load(path, 1) is not null;

    /// <summary>A frozen bitmap decoded to <paramref name="pixelWidth"/>; null for a file that cannot
    /// be read as an image, whatever way the decoder fails on it.</summary>
    internal static BitmapSource? LoadBitmap(string path, int pixelWidth)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = Math.Max(1, pixelWidth);
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The palette-normalised color of the icon's dominant hue, weighted by saturation and
    /// alpha over a 24-bin hue histogram; pixels (or filled shapes) with alpha below 128, saturation
    /// below 0.25, or lightness outside 0.15..0.9 are left out, the same way a near-transparent,
    /// near-gray or near-black/white area would tell a viewer nothing about the icon's own "color".
    /// A vector icon weighs each fill by the area its shape covers. Null for a file that fails to
    /// decode or has nothing left standing after that filter.</summary>
    internal static Color? DominantColor(string path)
    {
        try
        {
            return IsVectorFile(path) ? LoadVector(path)?.Hues.Dominant() : DominantBitmapColor(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A damaged file can fail the decoder in many ways; it just has no color of its own.
            return null;
        }
    }

    private static Color? DominantBitmapColor(string path)
    {
        BitmapSource frame;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return null;
            frame = decoder.Frames[0];
            // A tiny file can declare an enormous image; converting it would eat memory and time.
            if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
                return null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
        {
            return null;
        }

        var hues = new HueHistogram();
        AddBitmap(hues, frame, 1.0);
        return hues.Dominant();
    }

    private static void AddBitmap(HueHistogram hues, BitmapSource source, double weightPerPixel)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        if (width <= 0 || height <= 0)
            return;

        var stride = width * 4;
        var row = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            converted.CopyPixels(new Int32Rect(0, y, width, 1), row, stride, 0);
            for (var x = 0; x < stride; x += 4)
                hues.Add(Color.FromArgb(row[x + 3], row[x + 2], row[x + 1], row[x]), weightPerPixel);
        }
    }

    private static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>A file that exists, is no link (nor lies in a linked folder) and stays within <see
    /// cref="MaxFileBytes"/>.</summary>
    private static bool IsSafeFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaxFileBytes)
                return false;
            var folder = info.DirectoryName;
            return folder is not null && !IsLink(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>A 24-bin hue histogram of weighted colors.</summary>
    private sealed class HueHistogram
    {
        private const int Bins = 24;
        private readonly double[] _weight = new double[Bins];

        public void Add(Color color, double weight)
        {
            if (color.A < 128 || weight <= 0 || !double.IsFinite(weight))
                return;

            var (hue, saturation, lightness) = ChartPalette.HslOf(Color.FromRgb(color.R, color.G, color.B));
            if (saturation < 0.25 || lightness is < 0.15 or > 0.9)
                return;

            var bin = Math.Min(Bins - 1, (int)(hue / 360.0 * Bins));
            _weight[bin] += saturation * (color.A / 255.0) * weight;
        }

        public void Add(HueHistogram other)
        {
            for (var bin = 0; bin < Bins; bin++)
                _weight[bin] += other._weight[bin];
        }

        public Color? Dominant()
        {
            var bestBin = -1;
            var bestWeight = 0.0;
            for (var bin = 0; bin < Bins; bin++)
            {
                if (_weight[bin] > bestWeight)
                {
                    bestWeight = _weight[bin];
                    bestBin = bin;
                }
            }

            return bestBin < 0 ? null : ChartPalette.NormalizeToPaletteBand((bestBin + 0.5) * (360.0 / Bins));
        }
    }
}
