using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using AiUsage.Services;

namespace AiUsage.Stats.Pricing;

/// <summary>
/// The euro reference rate (US dollars per euro) used to show the cost estimate in euro. Read from the
/// European Central Bank's daily XML, cached in the machine-local cache folder. The XML is foreign
/// text: no DTD, no external entities, a size limit, and a rate outside a sane range is dropped.
/// </summary>
public sealed class EurRate
{
    internal const string CacheFileName = "eur-rate.json";

    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly Func<string> _cacheDirectory;

    public EurRate(Func<string> cacheDirectory) => _cacheDirectory = cacheDirectory;

    /// <summary>US dollars per euro, or null before the first successful download.</summary>
    public double? UsdPerEur { get; private set; }

    public DateTimeOffset? FetchedAt { get; private set; }

    public bool IsStale(DateTimeOffset now) => UsdPerEur is null || FetchedAt is not { } at || now - at >= MaxAge;

    /// <summary>The USD rate in the ECB daily file, or null when the text is not that file.</summary>
    public static double? ParseEcb(string xml)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1_000_000,
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Cube")
                    continue;
                if (reader.GetAttribute("currency") != "USD")
                    continue;
                if (double.TryParse(reader.GetAttribute("rate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)
                    && double.IsFinite(rate) && rate is > 0.2 and < 5)
                    return rate;
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            return null;
        }

        return null;
    }

    public void Set(double usdPerEur, DateTimeOffset fetchedAt, bool persist = true)
    {
        UsdPerEur = usdPerEur;
        FetchedAt = fetchedAt;
        if (!persist)
            return;

        try
        {
            var directory = _cacheDirectory();
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(new { usdPerEur, fetchedAt = fetchedAt.ToString("O", CultureInfo.InvariantCulture) });
            var path = Path.Combine(directory, CacheFileName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LogService.Shared.Log("WARN", "Euro rate cache could not be written: " + ex.GetType().Name);
        }
    }

    public void Load()
    {
        try
        {
            var path = Path.Combine(_cacheDirectory(), CacheFileName);
            if (!File.Exists(path))
                return;

            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("usdPerEur", out var rate) && rate.ValueKind == JsonValueKind.Number
                && rate.TryGetDouble(out var value) && double.IsFinite(value) && value is > 0.2 and < 5
                && root.TryGetProperty("fetchedAt", out var stamp) && stamp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                Set(value, at, persist: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
        {
            // A damaged cache means dollars until the next download.
        }
    }
}
