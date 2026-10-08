using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AiUsage.Stats;

/// <summary>One project's own color and, where one was found, the icon file it was read from - the
/// icon path is exposed purely for <see cref="Views.StatsWindow"/>'s "top projects" panel to draw
/// alongside the color, whether or not that particular icon actually had a usable dominant hue.
/// </summary>
public readonly record struct ProjectColorInfo(Color Color, string? IconPath);

/// <summary>
/// Picks one color per project folder for the "top projects" panel, in order: (1) the dominant hue
/// of a project's own icon file, if one exists and has a clear enough dominant color; (2) a color
/// word found in the project's own folder name ("red-tool" reads red); (3) otherwise a color not
/// already used by a provider or by another project resolved in the same call, picked by a stable
/// hash of the folder path so the same project keeps the same color across runs. Every color, from
/// whichever source, is normalised into the same saturation/lightness band <see
/// cref="ChartPalette.ForProvider"/> already uses for an unrecognised id, so a project never reads
/// brighter or darker than the providers and models drawn next to it.
///
/// Icon lookups only ever read a project folder, never write to it; the one file this class writes
/// is its own small JSON cache, keyed by folder path, so a project's color and icon path survive
/// between runs without re-walking the folder or re-decoding the icon every time the statistics
/// window opens.
/// </summary>
public static class ProjectColorResolver
{
    // Raised whenever the icon search changes what it finds, so colors computed from an older find are
    // recomputed once.
    private const int CacheVersion = 2;
    private const int MaxIconSearchDepth = 6;
    private const int DeclaredIconSearchDepth = 3;
    private const int MaxDirectoryEntriesInspected = 5000;
    private const long MaxIconFileBytes = 2 * 1024 * 1024;
    private const long MaxIconPixels = 4096L * 4096L;

    // Two projects landing on the same hue purely by hash coincidence would be as confusing as a
    // project sharing a provider's own hue - kept a little tighter than the 25 degrees
    // ChartPalette.ForProvider's own unknown-swatch fallback keeps from a real provider, since a
    // project list can run to ten entries where the provider list never grows past five.
    private const double MinHueDistanceFromOtherProjects = 18.0;

    private static readonly char[] NameSplitChars = ['-', '_', ' ', '.'];

    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "build", "packages",
        "addons", "test", "tests", ".gradle", "Pods", "vendor", "third_party", "venv",
    };

    // Reference swatches for each recognised color word - only each swatch's own hue is ever read
    // back off of it (see ColorWordHue), so the exact saturation/lightness picked here never itself
    // reaches a chart; NormalizeToPaletteBand overwrites both before the color is used.
    private static readonly Dictionary<string, Color> ColorWordSwatches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = Colors.Red,
        ["rot"] = Colors.Red,
        ["orange"] = Colors.Orange,
        ["yellow"] = Colors.Gold,
        ["gelb"] = Colors.Gold,
        ["green"] = Colors.Green,
        ["grün"] = Colors.Green,
        ["gruen"] = Colors.Green,
        ["teal"] = Colors.Teal,
        ["blue"] = Colors.Blue,
        ["blau"] = Colors.Blue,
        ["purple"] = Colors.Purple,
        ["lila"] = Colors.Purple,
        ["violet"] = Colors.Purple,
        ["violett"] = Colors.Purple,
        ["pink"] = Colors.DeepPink,
        ["brown"] = Colors.SaddleBrown,
        ["braun"] = Colors.SaddleBrown,
    };

    /// <summary>Resolves every folder in <paramref name="projectFolders"/>, in the order given -
    /// that order is what the stable-hash fallback's own exclusion set builds up over, so passing
    /// the same list in a different order can pick different fallback colors for the entries that
    /// need one, though never a color already spoken for earlier in this same call. Reuses
    /// <paramref name="cacheFilePath"/>'s stored entry for a folder whose icon path and last-write
    /// time have not changed since it was last computed; everything else (a first run, a changed or
    /// vanished icon, or a cache file from an unrecognised version) is recomputed and the cache
    /// rewritten to match.</summary>
    public static IReadOnlyDictionary<string, ProjectColorInfo> ResolveAll(IReadOnlyList<string> projectFolders, string cacheFilePath)
    {
        var cache = LoadCache(cacheFilePath);
        var nextCache = new Dictionary<string, CacheEntry>();
        var result = new Dictionary<string, ProjectColorInfo>();
        var usedHues = new List<double>(ChartPalette.ProviderHueList);

        foreach (var folder in projectFolders)
        {
            var iconPath = FindIconFile(folder);
            var iconWriteUtc = iconPath is not null ? SafeLastWriteUtc(iconPath) : null;

            if (cache.TryGetValue(folder, out var cached)
                && cached.IconPath == iconPath
                && cached.IconLastWriteUtc == iconWriteUtc
                && TryParseColor(cached.ColorHex, out var cachedColor))
            {
                result[folder] = new ProjectColorInfo(cachedColor, iconPath);
                nextCache[folder] = cached;
                usedHues.Add(ChartPalette.HslOf(cachedColor).H);
                continue;
            }

            var color = ResolveColor(folder, iconPath, usedHues);
            result[folder] = new ProjectColorInfo(color, iconPath);
            nextCache[folder] = new CacheEntry(iconPath, iconWriteUtc, ColorToHex(color));
            usedHues.Add(ChartPalette.HslOf(color).H);
        }

        SaveCache(cacheFilePath, nextCache);
        return result;
    }

    private static Color ResolveColor(string projectFolder, string? iconPath, IReadOnlyList<double> excludedHues)
    {
        if (iconPath is not null && DominantIconColor(iconPath) is { } iconColor)
            return iconColor;

        var projectName = Path.GetFileName(projectFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (ColorWordHue(projectName) is { } wordHue)
            return ChartPalette.NormalizeToPaletteBand(wordHue);

        var slots = ChartPalette.HueSlotsAvoiding(excludedHues, MinHueDistanceFromOtherProjects);
        var fallbackHue = ChartPalette.PickStableHue(projectFolder, slots);
        return ChartPalette.NormalizeToPaletteBand(fallbackHue);
    }

    /// <summary>A color word as its own whole name segment (split on '-', '_', ' ', '.') - "red-tool"
    /// matches, "credential" does not just because it contains "red".</summary>
    internal static double? ColorWordHue(string projectName)
    {
        foreach (var segment in projectName.Split(NameSplitChars, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ColorWordSwatches.TryGetValue(segment, out var swatch))
                return ChartPalette.HslOf(swatch).H;
        }
        return null;
    }

    /// <summary>Finds a project's icon file in two steps. First the places that name the icon on
    /// purpose, in the project folder and up to three levels below it: the <c>ApplicationIcon</c> of a
    /// <c>*.csproj</c>, the first <c>bundle.icon</c> entry of <c>src-tauri/tauri.conf.json</c>, the
    /// launcher icon of an Android manifest, <c>expo.icon</c> of an <c>app.json</c>, and
    /// <c>config/icon</c> of a Godot project. Then a breadth first walk (up to <see
    /// cref="MaxIconSearchDepth"/> levels, <see cref="MaxDirectoryEntriesInspected"/> entries, the usual
    /// build, dependency and hidden folders skipped) whose candidates are ranked by <see
    /// cref="NameRank"/>. Only existing .ico or .png files up to <see cref="MaxIconFileBytes"/> count.
    /// Null for a folder that no longer exists or holds no such file.</summary>
    internal static string? FindIconFile(string projectFolder)
    {
        if (!Directory.Exists(projectFolder))
            return null;

        var files = WalkProject(projectFolder);
        return FindDeclaredIcon(projectFolder, files) ?? PickIconByPriority(projectFolder, files.Select(file => file.Path));
    }

    private readonly record struct WalkedFile(string Path, int Depth);

    /// <summary>Breadth first over <paramref name="projectFolder"/>: every file found, with the depth
    /// of the folder it lies in (0 for the project folder itself).</summary>
    private static List<WalkedFile> WalkProject(string projectFolder)
    {
        var files = new List<WalkedFile>();
        var inspected = 0;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((projectFolder, 0));

        while (queue.Count > 0 && inspected < MaxDirectoryEntriesInspected)
        {
            var (dir, depth) = queue.Dequeue();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                if (inspected >= MaxDirectoryEntriesInspected)
                    break;
                inspected++;

                if (Directory.Exists(entry))
                {
                    // A junction or symlink can lead out of the project (or into a loop), so it is
                    // never entered.
                    if (depth < MaxIconSearchDepth && !IsSkippedDirectory(Path.GetFileName(entry)) && !IsReparsePoint(entry))
                        queue.Enqueue((entry, depth + 1));
                    continue;
                }

                files.Add(new WalkedFile(entry, depth));
            }
        }

        return files;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsSkippedDirectory(string name) => name.StartsWith('.') || SkippedDirectoryNames.Contains(name);

    private static readonly string[] AndroidDensities = ["xxxhdpi", "xxhdpi", "xhdpi", "hdpi", "mdpi"];

    /// <summary>The icon a project file names outright, or null when none does (or the named file is
    /// missing, too big or not an .ico/.png).</summary>
    private static string? FindDeclaredIcon(string projectFolder, List<WalkedFile> files)
    {
        foreach (var file in files.Where(file => file.Depth <= DeclaredIconSearchDepth))
        {
            var name = Path.GetFileName(file.Path);
            var directory = Path.GetDirectoryName(file.Path)!;
            string? declared = null;

            if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                declared = ReadFirstMatch(projectFolder, file.Path, ApplicationIconPattern, directory);
            else if (name.Equals("tauri.conf.json", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(directory).Equals("src-tauri", StringComparison.OrdinalIgnoreCase))
                declared = ReadTauriIcon(projectFolder, file.Path, directory);
            else if (name.Equals("app.json", StringComparison.OrdinalIgnoreCase))
                declared = ReadExpoIcon(projectFolder, file.Path, directory);
            else if (name.Equals("project.godot", StringComparison.OrdinalIgnoreCase))
                declared = ReadFirstMatch(projectFolder, file.Path, GodotIconPattern, directory, resourcePrefix: "res://");

            if (IsUsableIconFile(projectFolder, declared))
                return declared;
        }

        foreach (var mainFolder in new[] { Path.Combine(projectFolder, "app", "src", "main"), Path.Combine(projectFolder, "android", "app", "src", "main") })
        {
            var manifest = Path.Combine(mainFolder, "AndroidManifest.xml");
            if (File.Exists(manifest) && ReadAndroidIcon(projectFolder, manifest, mainFolder) is { } androidIcon)
                return androidIcon;
        }

        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex ApplicationIconPattern =
        new("<ApplicationIcon>\\s*([^<]+?)\\s*</ApplicationIcon>", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex GodotIconPattern =
        new("^\\s*config/icon\\s*=\\s*\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Multiline);

    private static readonly System.Text.RegularExpressions.Regex AndroidIconPattern =
        new("android:icon\\s*=\\s*\"@(mipmap|drawable)/([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string? ReadFirstMatch(string projectRoot, string filePath, System.Text.RegularExpressions.Regex pattern, string baseDirectory, string resourcePrefix = "")
    {
        try
        {
            var match = pattern.Match(File.ReadAllText(filePath));
            if (!match.Success)
                return null;

            var value = match.Groups[1].Value.Trim();
            if (resourcePrefix.Length > 0)
            {
                if (!value.StartsWith(resourcePrefix, StringComparison.Ordinal))
                    return null;
                value = value[resourcePrefix.Length..];
            }

            return CombineRelative(projectRoot, baseDirectory, value);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? ReadTauriIcon(string projectRoot, string filePath, string baseDirectory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            // Tauri 2 keeps "bundle" at the top level, Tauri 1 below "tauri".
            if (!root.TryGetProperty("bundle", out var bundle)
                && !(root.TryGetProperty("tauri", out var tauri) && tauri.ValueKind == JsonValueKind.Object && tauri.TryGetProperty("bundle", out bundle)))
                return null;
            if (bundle.ValueKind != JsonValueKind.Object || !bundle.TryGetProperty("icon", out var icons) || icons.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var icon in icons.EnumerateArray())
            {
                if (icon.ValueKind != JsonValueKind.String || icon.GetString() is not { } relative)
                    continue;
                if (!HasIconExtension(relative))
                    continue;
                var full = CombineRelative(projectRoot, baseDirectory, relative);
                if (IsUsableIconFile(projectRoot, full))
                    return full;
            }

            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    private static string? ReadExpoIcon(string projectRoot, string filePath, string baseDirectory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("expo", out var expo) && expo.ValueKind == JsonValueKind.Object
                && expo.TryGetProperty("icon", out var icon) && icon.ValueKind == JsonValueKind.String
                && icon.GetString() is { } relative)
                return CombineRelative(projectRoot, baseDirectory, relative);

            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    internal static string? ReadAndroidIcon(string projectRoot, string manifestPath, string mainFolder)
    {
        string text;
        try { text = File.ReadAllText(manifestPath); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var match = AndroidIconPattern.Match(text);
        if (!match.Success)
            return null;

        var kind = match.Groups[1].Value;
        var iconName = match.Groups[2].Value;
        var resFolder = Path.Combine(mainFolder, "res");
        foreach (var density in AndroidDensities)
        {
            var candidate = Path.Combine(resFolder, kind + "-" + density, iconName + ".png");
            if (IsUsableIconFile(projectRoot, candidate))
                return candidate;
        }

        var plain = Path.Combine(resFolder, kind, iconName + ".png");
        return IsUsableIconFile(projectRoot, plain) ? plain : null;
    }

    private static bool HasIconExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".ico", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A path a project file names, resolved against <paramref name="baseDirectory"/> and kept
    /// only when it stays inside <paramref name="projectRoot"/>. A rooted, UNC or escaping path is
    /// refused: a cloned project must not be able to point the lookup at another folder or at a
    /// network share (which would open a connection just to probe the file).</summary>
    internal static string? CombineRelative(string projectRoot, string baseDirectory, string relative)
    {
        try
        {
            var normalized = relative.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized))
                return null;

            var full = Path.GetFullPath(Path.Combine(baseDirectory, normalized));
            var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>The single gate every icon candidate passes before any file is touched: it must lie
    /// inside <paramref name="projectRoot"/> (a rooted, UNC or escaping name is refused by string
    /// comparison alone, so no network share is probed), be an .ico or .png, and neither the file nor
    /// a folder between the root and the file may be a link. Then the file must exist and stay
    /// within <see cref="MaxIconFileBytes"/>.</summary>
    internal static bool IsUsableIconFile(string projectRoot, string? path)
    {
        if (path is null || !HasIconExtension(path))
            return false;

        try
        {
            var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;

            for (var dir = Path.GetDirectoryName(full); dir is { Length: > 0 } && dir.Length > root.Length; dir = Path.GetDirectoryName(dir))
                if (IsReparsePoint(dir))
                    return false;

            var info = new FileInfo(full);
            return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.Length <= MaxIconFileBytes;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    // The fixed order of well-known icon names; an earlier name beats a later one wherever in the
    // walk either turned up. After the last of these any plain .ico counts. The project folder's own
    // name plus ".ico" slots in after app.ico.
    private static readonly string[] NamePriorityBeforeProjectName = ["app.ico"];
    private static readonly string[] NamePriorityAfterProjectName =
        ["icon.ico", "favicon.ico", "app-mark.png", "appicon.png", "icon.png", "ic_launcher.png", "logo.png"];

    private static readonly string[] ExcludedNameParts = ["splash", "background", "foreground", "monochrome"];

    /// <summary>Lower is better; <see cref="int.MaxValue"/> means "not a candidate".</summary>
    private static int NameRank(string fileName, string projectName)
    {
        foreach (var part in ExcludedNameParts)
            if (fileName.Contains(part, StringComparison.OrdinalIgnoreCase))
                return int.MaxValue;

        var index = 0;
        foreach (var name in NamePriorityBeforeProjectName)
        {
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                return index;
            index++;
        }

        if (projectName.Length > 0 && string.Equals(projectName + ".ico", fileName, StringComparison.OrdinalIgnoreCase))
            return index;
        index++;

        foreach (var name in NamePriorityAfterProjectName)
        {
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                return index;
            index++;
        }

        return string.Equals(Path.GetExtension(fileName), ".ico", StringComparison.OrdinalIgnoreCase) ? index : int.MaxValue;
    }

    /// <summary>Among equally ranked launcher icons the one in the densest mipmap/drawable folder
    /// wins; any other folder counts as least dense.</summary>
    private static int DensityRank(string filePath)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "");
        var index = Array.FindIndex(AndroidDensities, density => folder.EndsWith("-" + density, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : AndroidDensities.Length;
    }

    private static string? PickIconByPriority(string projectFolder, IEnumerable<string> candidates)
    {
        var projectName = Path.GetFileName(projectFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string? best = null;
        var bestRank = int.MaxValue;
        var bestDensity = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (!HasIconExtension(candidate))
                continue;

            var rank = NameRank(Path.GetFileName(candidate), projectName);
            if (rank == int.MaxValue)
                continue;

            var density = DensityRank(candidate);
            if (rank > bestRank || (rank == bestRank && density >= bestDensity))
                continue;
            if (!IsUsableIconFile(projectFolder, candidate))
                continue;

            best = candidate;
            bestRank = rank;
            bestDensity = density;
        }

        return best;
    }

    /// <summary>Decodes <paramref name="iconFilePath"/> and returns the palette-normalised color of
    /// its dominant hue, weighted by saturation and alpha over a 24-bin hue histogram; pixels with
    /// alpha below 128, saturation below 0.25, or lightness outside 0.15..0.9 are left out of the
    /// histogram entirely, the same way a near-transparent, near-gray or near-black/white pixel
    /// would tell a viewer nothing about the icon's own "color". Null for a file that fails to
    /// decode, has no frames, or has no pixel left standing after that filter.</summary>
    internal static Color? DominantIconColor(string iconFilePath)
    {
        BitmapSource frame;
        try
        {
            using var stream = File.OpenRead(iconFilePath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return null;
            frame = decoder.Frames[0];
            // A tiny file can declare an enormous image; converting it would eat memory and time.
            if ((long)frame.PixelWidth * frame.PixelHeight > MaxIconPixels)
                return null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
        {
            return null;
        }

        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        if (width <= 0 || height <= 0)
            return null;

        const int bins = 24;
        var weightByBin = new double[bins];
        var stride = width * 4;
        var row = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            converted.CopyPixels(new Int32Rect(0, y, width, 1), row, stride, 0);
            for (var x = 0; x < stride; x += 4)
            {
                var b = row[x];
                var g = row[x + 1];
                var r = row[x + 2];
                var a = row[x + 3];
                if (a < 128)
                    continue;

                var (hue, saturation, lightness) = ChartPalette.HslOf(Color.FromRgb(r, g, b));
                if (saturation < 0.25 || lightness is < 0.15 or > 0.9)
                    continue;

                var bin = Math.Min(bins - 1, (int)(hue / 360.0 * bins));
                weightByBin[bin] += saturation * (a / 255.0);
            }
        }

        var bestBin = -1;
        var bestWeight = 0.0;
        for (var bin = 0; bin < bins; bin++)
        {
            if (weightByBin[bin] > bestWeight)
            {
                bestWeight = weightByBin[bin];
                bestBin = bin;
            }
        }

        if (bestBin < 0)
            return null;

        var binCenterHue = (bestBin + 0.5) * (360.0 / bins);
        return ChartPalette.NormalizeToPaletteBand(binCenterHue);
    }

    private static DateTime? SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string ColorToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool TryParseColor(string hex, out Color color)
    {
        color = default;
        if (hex.Length != 7 || hex[0] != '#')
            return false;
        if (!byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return false;
        color = Color.FromRgb(r, g, b);
        return true;
    }

    private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = true };

    private static Dictionary<string, CacheEntry> LoadCache(string cacheFilePath)
    {
        try
        {
            if (!File.Exists(cacheFilePath))
                return [];

            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cacheFilePath), CacheJsonOptions);
            if (file is null || file.Version != CacheVersion)
                return [];

            return file.Entries ?? [];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        catch (JsonException) { return []; }
    }

    private static void SaveCache(string cacheFilePath, Dictionary<string, CacheEntry> entries)
    {
        try
        {
            var directory = Path.GetDirectoryName(cacheFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var file = new CacheFile { Version = CacheVersion, Entries = entries };
            File.WriteAllText(cacheFilePath, JsonSerializer.Serialize(file, CacheJsonOptions));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public Dictionary<string, CacheEntry>? Entries { get; set; }
    }

    internal sealed record CacheEntry(string? IconPath, DateTime? IconLastWriteUtc, string ColorHex);
}
