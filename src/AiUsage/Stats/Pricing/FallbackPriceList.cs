using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiUsage.Services;

namespace AiUsage.Stats.Pricing;

/// <summary>
/// The second price table behind the cost estimate, asked only for a model the main list has no price
/// for. It is a trimmed copy of a public table (providers, each with models and a price per million
/// tokens) in the machine-local cache folder. Like the main list it is foreign text: only the ids and
/// four price fields are read, every price must be a finite number in a sane range and anything else is
/// dropped. It is downloaded at most once a day and only after the main list has met an unknown model.
/// </summary>
internal sealed class FallbackPriceList
{
    internal const string CacheFileName = "model-prices-extra.json";

    private const double MaxTokenPrice = 0.01;
    private const int MaxEntries = 100_000;
    private const int MaxIdLength = 200;

    /// <summary>The trimmed cache is far smaller than the download; a bigger file is not ours.</summary>
    internal const long MaxCacheBytes = 16L * 1024 * 1024;

    private static readonly ModelPrice FreePrice = new(0, 0, null, null);

    private static readonly Regex DateSuffix = new(@"-(\d{8}|\d{4}-\d{2}-\d{2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>One model of one provider, with its price per token.</summary>
    internal sealed record Entry(string Provider, string Model, ModelPrice Price)
    {
        internal bool IsZero => Price.Input == 0 && Price.Output == 0;
    }

    private readonly Func<string> _cacheDirectory;
    private readonly object _gate = new();
    private Dictionary<string, List<Entry>> _index = new(StringComparer.Ordinal);
    private int _count;
    private int _wanted;

    internal FallbackPriceList(Func<string> cacheDirectory) => _cacheDirectory = cacheDirectory;

    /// <summary>When the table was last downloaded; null while there is none.</summary>
    internal DateTimeOffset? FetchedAt { get; private set; }

    internal bool IsEmpty
    {
        get
        {
            lock (_gate)
                return _count == 0;
        }
    }

    internal bool IsStale(DateTimeOffset now) => IsEmpty || FetchedAt is not { } at || now - at >= ModelPriceList.MaxAge;

    /// <summary>True once the main list has met a model it has no price for in this run.</summary>
    internal bool Wanted => Volatile.Read(ref _wanted) == 1;

    /// <summary>Raised on the calling thread the first time <see cref="MarkWanted"/> is called.</summary>
    internal event Action? WantedRaised;

    internal void MarkWanted()
    {
        if (Interlocked.Exchange(ref _wanted, 1) == 0)
            WantedRaised?.Invoke();
    }

    /// <summary>Reads the downloaded table. Never throws: text that is not the expected JSON gives an
    /// empty result. A model needs a valid input and output price; the cache prices are optional.</summary>
    internal static IReadOnlyList<Entry> Parse(string json)
    {
        var result = new List<Entry>();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var provider in document.RootElement.EnumerateObject())
            {
                if (provider.Value.ValueKind != JsonValueKind.Object || provider.Name.Length is 0 or > MaxIdLength
                    || !provider.Value.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var model in models.EnumerateObject())
                {
                    if (result.Count >= MaxEntries)
                        return result;
                    if (model.Value.ValueKind != JsonValueKind.Object || model.Name.Length is 0 or > MaxIdLength
                        || !model.Value.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
                        continue;

                    var input = PerMillion(cost, "input");
                    var output = PerMillion(cost, "output");
                    if (input is null || output is null)
                        continue;

                    var id = model.Value.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                        && idValue.GetString() is { Length: > 0 and <= MaxIdLength } text ? text : model.Name;
                    result.Add(new Entry(
                        provider.Name.ToLowerInvariant(), id,
                        new ModelPrice(input.Value, output.Value, PerMillion(cost, "cache_read"), PerMillion(cost, "cache_write"))));
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return result;
    }

    private static double? PerMillion(JsonElement cost, string name)
    {
        if (!cost.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
            return null;
        var perToken = number / 1_000_000;
        return double.IsFinite(perToken) && perToken >= 0 && perToken < MaxTokenPrice ? perToken : null;
    }

    /// <summary>Swaps in a new table and, when asked, writes the trimmed cache file.</summary>
    internal void Replace(IReadOnlyList<Entry> entries, DateTimeOffset fetchedAt, bool persist = true)
    {
        var index = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var id = entry.Model.ToLowerInvariant();
            Add(index, id, entry);
            var last = LastSegment(id);
            if (last != id)
                Add(index, last, entry);
        }

        lock (_gate)
        {
            _index = index;
            _count = entries.Count;
            FetchedAt = fetchedAt;
        }

        if (persist)
            Save(entries, fetchedAt);
    }

    private static void Add(Dictionary<string, List<Entry>> index, string key, Entry entry)
    {
        if (!index.TryGetValue(key, out var list))
            index[key] = list = [];
        list.Add(entry);
    }

    private static string LastSegment(string name) => name[(name.LastIndexOf('/') + 1)..];

    private void Save(IReadOnlyList<Entry> entries, DateTimeOffset fetchedAt)
    {
        try
        {
            var directory = _cacheDirectory();
            Directory.CreateDirectory(directory);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("fetchedAt", fetchedAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteStartArray("models");
                foreach (var entry in entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("provider", entry.Provider);
                    writer.WriteString("id", entry.Model);
                    writer.WriteNumber("input", entry.Price.Input);
                    writer.WriteNumber("output", entry.Price.Output);
                    if (entry.Price.CacheRead is { } read)
                        writer.WriteNumber("cacheRead", read);
                    if (entry.Price.CacheCreation is { } creation)
                        writer.WriteNumber("cacheCreation", creation);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            var path = Path.Combine(directory, CacheFileName);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, stream.ToArray());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LogService.Shared.Log("WARN", "Second model price cache could not be written: " + ex.GetType().Name);
        }
    }

    /// <summary>Loads the cache file written by the last download. A missing or damaged file leaves the
    /// table empty.</summary>
    internal void Load()
    {
        try
        {
            var path = Path.Combine(_cacheDirectory(), CacheFileName);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxCacheBytes)
                return;

            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("fetchedAt", out var stamp) || stamp.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fetchedAt))
                return;

            var loaded = new List<Entry>();
            foreach (var item in models.EnumerateArray())
            {
                if (loaded.Count >= MaxEntries)
                    break;
                if (item.ValueKind != JsonValueKind.Object
                    || !Text(item, "provider", out var provider) || !Text(item, "id", out var id)
                    || Number(item, "input") is not { } input || Number(item, "output") is not { } output)
                    continue;
                loaded.Add(new Entry(provider, id, new ModelPrice(input, output, Number(item, "cacheRead"), Number(item, "cacheCreation"))));
            }

            if (loaded.Count > 0)
                Replace(loaded, fetchedAt, persist: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
        {
            // A damaged cache means no second source until the next download.
        }
    }

    private static bool Text(JsonElement item, string name, out string text)
    {
        text = item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        return text.Length is > 0 and <= MaxIdLength;
    }

    private static double? Number(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
            return null;
        return double.IsFinite(number) && number >= 0 && number < MaxTokenPrice ? number : null;
    }

    /// <summary>The price of <paramref name="model"/>, or null when no entry carries its name. The full
    /// name and its last segment after "/" are compared with each entry's id and the id's last segment,
    /// ignoring case; a date suffix is tried only when the plain name matches nothing. Entries that cost
    /// nothing are ignored unless every match is free (then the model is free). An entry filed under the
    /// model's own maker wins; otherwise the match with the median input price is taken.</summary>
    internal ModelPrice? Find(string model) => Find(Current, model);

    /// <summary>The table as it is now. A later <see cref="Replace"/> swaps in a new one and never
    /// changes this one, so a caller can keep it for a consistent run.</summary>
    internal IReadOnlyDictionary<string, List<Entry>> Current
    {
        get
        {
            lock (_gate)
                return _index;
        }
    }

    /// <summary><see cref="Find(string)"/> against a table taken earlier from <see cref="Current"/>.</summary>
    internal static ModelPrice? Find(IReadOnlyDictionary<string, List<Entry>> index, string model)
    {
        var name = model.Trim().ToLowerInvariant();
        if (name.Length == 0 || index.Count == 0)
            return null;

        var last = LastSegment(name);
        var matches = Collect(index, name, last);
        if (matches.Count == 0)
            matches = Collect(index, DateSuffix.Replace(name, ""), DateSuffix.Replace(last, ""));
        if (matches.Count == 0)
            return null;

        var paid = matches.Where(entry => !entry.IsZero).ToList();
        if (paid.Count == 0)
            return FreePrice;

        var family = ModelPriceList.Family(last);
        var own = paid.Where(entry => entry.Provider == family || MakerOf(entry.Model.ToLowerInvariant()) == family).ToList();
        var ordered = (own.Count > 0 ? own : paid)
            .OrderBy(entry => entry.Price.Input)
            .ThenBy(entry => entry.Provider, StringComparer.Ordinal)
            .ThenBy(entry => entry.Model, StringComparer.Ordinal)
            .ToList();
        return ordered[(ordered.Count - 1) / 2].Price;
    }

    private static string MakerOf(string id)
    {
        var slash = id.LastIndexOf('/');
        if (slash < 0)
            return "";
        var before = id[..slash];
        return before[(before.LastIndexOf('/') + 1)..];
    }

    private static List<Entry> Collect(IReadOnlyDictionary<string, List<Entry>> index, string first, string second)
    {
        var found = new List<Entry>();
        foreach (var key in first == second ? [first] : new[] { first, second })
            if (key.Length > 0 && index.TryGetValue(key, out var list))
                foreach (var entry in list)
                    if (!found.Contains(entry))
                        found.Add(entry);
        return found;
    }
}
