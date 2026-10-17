using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// Draws the tray icon itself: a filled rounded square in the level colour with the rounded usage
/// percentage centred on it, so the one question the tray icon exists to answer ("how close am I")
/// has an actual number on it, not colour alone. Pure <see cref="System.Drawing"/> drawing code -
/// no live desktop session needed, which is what keeps it unit-testable.
/// </summary>
public static class TrayIconRenderer
{
    // Deeper shades of the shipped tray-ok/-warn/-crit.ico colours, so white digits stay readable on
    // every level (white on the bright amber would not be).
    private static readonly Color OkColor = Color.FromArgb(0x6D, 0x28, 0xD9);
    private static readonly Color WarnColor = Color.FromArgb(0xB4, 0x53, 0x09);
    private static readonly Color CritColor = Color.FromArgb(0xBE, 0x12, 0x3C);

    // Old values: the level colour would claim a state that may no longer hold, so the tile goes grey.
    private static readonly Color DimmedColor = Color.FromArgb(0x61, 0x61, 0x61);

    /// <summary>The fixed background colour for a level - not a theme lookup, so the icon reads the
    /// same on every taskbar.</summary>
    internal static Color BackgroundColor(UsageLevel level) => level switch
    {
        UsageLevel.Crit => CritColor,
        UsageLevel.Warn => WarnColor,
        _ => OkColor,
    };

    // Drawing the digits means measuring several candidate sizes, so a finished icon is kept per
    // (level, percent, size, dimmed) and handed out as a copy, and the font size that fit a text is kept per
    // (text, size). GDI+ objects are not safe to share between threads, so all of it runs under one lock.
    private const int MaxCachedIcons = 128;
    private static readonly object Gate = new();
    private static readonly Dictionary<(UsageLevel Level, int Percent, int Size, bool Dimmed), Bitmap> Rendered = [];
    private static readonly Dictionary<(string Text, int Size), int> ChosenPixels = [];
    private static readonly Dictionary<bool, (FontFamily Family, System.Drawing.FontStyle Style)> Faces = [];

    /// <summary>How many icons were drawn from scratch (a cache hit does not count); for tests.</summary>
    internal static int ComposeCount { get; private set; }

    /// <summary>Renders one square tray-icon bitmap for the given level and percent, at
    /// <paramref name="size"/> pixels - the small-icon size Windows asks for at the current scaling
    /// (16, 20, 24 or 32), see <see cref="IconSizeFor"/>. The digits are hinted text (crisp on the
    /// pixel grid), as large as the margin allows, and their ink box sits in the middle of the tile on
    /// both axes. <paramref name="dimmed"/> draws grey instead of the level colour (the numbers are old).
    /// The caller owns the returned bitmap and must dispose it.</summary>
    public static Bitmap Render(UsageLevel level, int percent, int size, bool dimmed = false)
    {
        var key = (level, Math.Clamp(percent, 0, 100), size, dimmed);
        lock (Gate)
        {
            if (!Rendered.TryGetValue(key, out var master))
            {
                if (Rendered.Count >= MaxCachedIcons)
                {
                    foreach (var cached in Rendered.Values)
                        cached.Dispose();
                    Rendered.Clear();
                }

                master = Compose(level, percent, size, dimmed);
                Rendered[key] = master;
            }

            return new Bitmap(master);
        }
    }

    private static Bitmap Compose(UsageLevel level, int percent, int size, bool dimmed)
    {
        ComposeCount++;
        var background = dimmed ? DimmedColor : BackgroundColor(level);
        var textColor = ContrastingTextColor(background);

        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using (var path = RoundedSquare(size, size / 5f))
        using (var brush = new SolidBrush(background))
            g.FillPath(brush, path);

        using var digits = DigitsBitmap(FormatPercent(percent), size, textColor, out var ink);
        var (left, top) = CentredOrigin(digits, ink, size);
        // A one-to-one pixel copy: the default resampling would smear the digits by half a pixel.
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(digits, new Rectangle(left, top, ink.Width, ink.Height), ink, GraphicsUnit.Pixel);

        return bitmap;
    }

    /// <summary>The icon size to draw for the shell's small-icon metric: exactly what Windows asks
    /// for (16, 20, 24, 32 at 100 to 200 % scaling), so the shell never has to rescale the bitmap
    /// and blur it. Anything outside a sane range falls back to the nearest limit.</summary>
    internal static int IconSizeFor(int smallIconMetric) => Math.Clamp(smallIconMetric, 16, 64);

    /// <summary>Clear of the rounded corners (radius size / 5), more room the larger the tile.</summary>
    internal static int Margin(int size) => size >= 32 ? 4 : size >= 24 ? 3 : 2;

    // The face is looked up once per kind of text and kept for the process.
    private static (FontFamily Family, System.Drawing.FontStyle Style) DigitFace(int digitCount)
    {
        var condensed = digitCount >= 3;
        if (!Faces.TryGetValue(condensed, out var face))
        {
            face = FindDigitFace(condensed);
            Faces[condensed] = face;
        }

        return face;
    }

    // Bahnschrift semibold, semi-condensed for one or two digits and condensed for "100", so the
    // three digits keep almost the same height without being squeezed. GDI+ knows the semi-condensed
    // face by its name cut to 31 characters; older images lack Bahnschrift, so Segoe UI Semibold
    // follows.
    private static (FontFamily Family, System.Drawing.FontStyle Style) FindDigitFace(bool condensed)
    {
        foreach (var (name, style) in new[]
                 {
                     (condensed ? "Bahnschrift SemiBold Condensed" : "Bahnschrift SemiBold SemiConden", System.Drawing.FontStyle.Regular),
                     ("Bahnschrift SemiBold SemiCondensed", System.Drawing.FontStyle.Regular),
                     ("Bahnschrift", System.Drawing.FontStyle.Bold),
                     ("Segoe UI Semibold", System.Drawing.FontStyle.Regular),
                 })
        {
            try
            {
                return (new FontFamily(name), style);
            }
            catch (ArgumentException)
            {
            }
        }

        return (FontFamily.GenericSansSerif, System.Drawing.FontStyle.Bold);
    }

    /// <summary>The digits drawn as hinted text on a transparent scratch bitmap, at the largest
    /// whole pixel size whose ink fits the tile inside <see cref="Margin"/> on every side, plus the
    /// ink box measured from the pixels themselves (not from font metrics, which carry side bearings
    /// and line spacing that would pull the number off centre). When the free height left by that
    /// size is odd, the next smaller size with an even remainder wins if it is at most one pixel
    /// shorter: hinted digits cannot move by half a pixel, and an odd remainder would always leave
    /// the number one pixel high.</summary>
    internal static Bitmap DigitsBitmap(string text, int size, Color color, out Rectangle ink)
    {
        lock (Gate)
        {
            var box = size - 2 * Margin(size);
            var (family, style) = DigitFace(text.Length);

            // The size that fit this text before is drawn straight away.
            if (ChosenPixels.TryGetValue((text, size), out var known))
            {
                var remembered = DrawDigits(text, family, style, known, size, color);
                ink = InkBounds(remembered);
                return remembered;
            }

            Bitmap? best = null;
            var bestInk = Rectangle.Empty;
            var bestPixels = 4;
            for (var pixels = (int)Math.Floor(size * 0.8); pixels >= 4; pixels--)
            {
                var scratch = DrawDigits(text, family, style, pixels, size, color);
                var candidate = InkBounds(scratch);
                if (candidate.Width > box || candidate.Height > box)
                {
                    scratch.Dispose();
                    continue;
                }

                if (best is null)
                {
                    (best, bestInk, bestPixels) = (scratch, candidate, pixels);
                    if ((size - candidate.Height) % 2 == 0)
                        break;
                    continue;
                }

                if (candidate.Height < bestInk.Height - 1)
                {
                    scratch.Dispose();
                    break;
                }

                if ((size - candidate.Height) % 2 == 0)
                {
                    best.Dispose();
                    (best, bestInk, bestPixels) = (scratch, candidate, pixels);
                    break;
                }

                scratch.Dispose();
            }

            if (best is null)
            {
                best = DrawDigits(text, family, style, 4, size, color);
                bestInk = InkBounds(best);
            }

            ChosenPixels[(text, size)] = bestPixels;
            ink = bestInk;
            return best;
        }
    }

    private static Bitmap DrawDigits(string text, FontFamily family, System.Drawing.FontStyle style, int pixels, int size, Color color)
    {
        var scratch = new Bitmap(size * 2, size * 2);
        using var g = Graphics.FromImage(scratch);
        using var font = new Font(family, pixels, style, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        g.Clear(Color.Transparent);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.DrawString(text, font, brush, size / 2f, size / 2f, StringFormat.GenericTypographic);
        return scratch;
    }

    /// <summary>Where the ink box goes on the tile so the digits look centred. The box edges are
    /// weighted by how strongly their outermost row or column is covered: a faint anti-aliased
    /// fringe counts as part of a pixel, not a whole one, so "42" with a pale right edge is not
    /// pushed a full pixel left. The result is rounded to whole pixels to keep the hinting crisp.</summary>
    internal static (int Left, int Top) CentredOrigin(Bitmap digits, Rectangle ink, int size)
    {
        if (ink.IsEmpty)
            return (0, 0);

        var alpha = ReadAlpha(digits, out var width);

        double Coverage(bool column, int index)
        {
            var max = 0;
            var length = column ? ink.Height : ink.Width;
            for (var i = 0; i < length; i++)
            {
                var value = column ? alpha[(ink.Top + i) * width + index] : alpha[index * width + ink.Left + i];
                max = Math.Max(max, value);
            }
            return max / 255.0;
        }

        double Origin(int extent, double leadingCoverage, double trailingCoverage)
        {
            // Visual extent inside the box: the leading edge starts late by the missing coverage,
            // the trailing edge ends early by it.
            var start = 1 - leadingCoverage;
            var end = extent - (1 - trailingCoverage);
            var centre = (start + end) / 2;
            return size / 2.0 - centre;
        }

        var left = Origin(ink.Width, Coverage(true, ink.Left), Coverage(true, ink.Right - 1));
        var top = Origin(ink.Height, Coverage(false, ink.Top), Coverage(false, ink.Bottom - 1));
        return ((int)Math.Round(left, MidpointRounding.AwayFromZero), (int)Math.Round(top, MidpointRounding.AwayFromZero));
    }

    /// <summary>The smallest rectangle holding every pixel with any coverage at all.</summary>
    internal static Rectangle InkBounds(Bitmap bitmap)
    {
        var alpha = ReadAlpha(bitmap, out var width);
        int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (alpha[y * width + x] == 0)
                continue;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>The alpha channel of every pixel, row by row, read in one pass with LockBits instead of
    /// a GetPixel call per pixel.</summary>
    private static byte[] ReadAlpha(Bitmap bitmap, out int width)
    {
        width = bitmap.Width;
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            var alpha = new byte[bitmap.Width * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < bitmap.Width; x++)
                    alpha[y * bitmap.Width + x] = row[x * 4 + 3];
            }

            return alpha;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>The number on the icon: the rounded percent clamped to 0 to 100, so a value past a
    /// full gauge (which should never happen) still reads "100" instead of a longer number.</summary>
    internal static string FormatPercent(int percent) =>
        Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture);

    /// <summary>White or black, whichever gives the higher WCAG contrast ratio against <paramref
    /// name="background"/>, measured by <see cref="AccentColors.Contrast"/>.</summary>
    internal static Color ContrastingTextColor(Color background)
    {
        var measured = ToMedia(background);
        return AccentColors.Contrast(measured, ToMedia(Color.White)) >= AccentColors.Contrast(measured, ToMedia(Color.Black))
            ? Color.White
            : Color.Black;
    }

    private static System.Windows.Media.Color ToMedia(Color color) =>
        System.Windows.Media.Color.FromRgb(color.R, color.G, color.B);

    private static GraphicsPath RoundedSquare(float size, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(size - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(size - diameter, size - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, size - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
