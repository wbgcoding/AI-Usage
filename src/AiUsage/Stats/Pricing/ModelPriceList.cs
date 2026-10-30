using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiUsage.Services;

namespace AiUsage.Stats.Pricing;

/// <summary>The public API list price of one model in US dollars per token. The two cache prices are
/// null when the list has none; the cost math then falls back to <see cref="Input"/>.</summary>
public sealed record ModelPrice(double Input, double Output, double? CacheRead, double? CacheCreation);

/// <summary>
/// The price list behind the cost estimate: a trimmed copy of a public price table that lives in the
/// machine-local cache folder. The table is foreign text, so it is read defensively: only four price
/// fields and two labels per entry are looked at, every price must be a finite number in a sane range,
/// and anything else is dropped. No table ships with the app; before the first download there are no
/// prices and nothing is estimated. A model this list does not know is looked up in a second, lazily
/// downloaded table (<see cref="FallbackPriceList"/>).
/// </summary>
public sealed class ModelPriceList
{
    internal const string CacheFileName = "model-prices.json";

    /// <summary>The list is fetched again once it is older than this.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>A per-token price at or above this is not a real price (that would be 10 dollars per
    /// 1000 tokens) and the field is dropped.</summary>
    private const double MaxTokenPrice = 0.01;

    private static readonly string[] MakerPrefixes = ["anthropic/", "openai/", "gemini/", "vertex_ai/"];

    private static readonly ModelPrice FreePrice = new(0, 0, null, null);

    // Models no price table will ever list (internal tool models without a public API price): their
    // tokens stay unpriced, but a warning at every start would only be noise.
    private static readonly HashSet<string> KnownUnpriced = new(StringComparer.OrdinalIgnoreCase) { "codex-auto-review" };

    private static readonly Regex FreeWord = new(@"(^|[-:_./@])free($|[-:_./@])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex AliasSuffix = new(@"-(default|[a-z])$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DateSuffix = new(@"-(\d{8}|\d{4}-\d{2}-\d{2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Func<string> _cacheDirectory;
    private readonly object _gate;
    private readonly Dictionary<string, ModelPrice?> _found = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported;
    private Dictionary<string, ModelPrice> _prices = new();

    // A snapshot keeps the second table it was taken with; the live list reads the current one.
    private readonly IReadOnlyDictionary<string, List<FallbackPriceList.Entry>>? _fallbackFrozen;

    public ModelPriceList(Func<string> cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        _gate = new object();
        _reported = new HashSet<string>(StringComparer.Ordinal);
        Fallback = new FallbackPriceList(cacheDirectory);
    }

    private ModelPriceList(ModelPriceList source)
    {
        _cacheDirectory = source._cacheDirectory;
        _gate = source._gate;
        _reported = source._reported;
        Fallback = source.Fallback;
        _fallbackFrozen = source._fallbackFrozen ?? source.Fallback.Current;
        lock (_gate)
        {
            _prices = source._prices;
            FetchedAt = source.FetchedAt;
        }
    }

    /// <summary>The prices as they are now, unaffected by a later <see cref="Replace"/> on this list.
    /// A model without a price is still reported once per run across both lists.</summary>
    internal ModelPriceList Snapshot() => new(this);

    /// <summary>The second table, asked only for a model this list has no price for. Shared with every
    /// snapshot.</summary>
    internal FallbackPriceList Fallback { get; }

    /// <summary>Swaps in a new second table; remembered answers are dropped so the models it prices are found.</summary>
    internal void ReplaceFallback(IReadOnlyList<FallbackPriceList.Entry> entries, DateTimeOffset fetchedAt, bool persist = true)
    {
        Fallback.Replace(entries, fetchedAt, persist);
        lock (_gate)
            _found.Clear();
    }

    /// <summary>When the prices were last downloaded; null while there are none.</summary>
    public DateTimeOffset? FetchedAt { get; private set; }

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
                return _prices.Count == 0;
        }
    }

    /// <summary>True when there are no prices yet or they are a day old.</summary>
    public bool IsStale(DateTimeOffset now) => IsEmpty || FetchedAt is not { } at || now - at >= MaxAge;

    /// <summary>Reads the downloaded table. Never throws: text that is not the expected JSON gives an
    /// empty result. Kept are chat and responses models and entries without a mode; an entry needs a
    /// valid input and output price, the two cache prices are optional.</summary>
    public static IReadOnlyDictionary<string, ModelPrice> Parse(string json)
    {
        var result = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object
                    || string.Equals(entry.Name, "sample_spec", StringComparison.Ordinal)
                    || entry.Name.Length is 0 or > 200)
                    continue;

                if (entry.Value.TryGetProperty("mode", out var mode)
                    && !(mode.ValueKind == JsonValueKind.String && mode.GetString() is "chat" or "responses"))
                    continue;

                var input = Price(entry.Value, "input_cost_per_token");
                var output = Price(entry.Value, "output_cost_per_token");
                if (input is null || output is null)
                    continue;

                result[entry.Name] = new ModelPrice(
                    input.Value, output.Value,
                    Price(entry.Value, "cache_read_input_token_cost"),
                    Price(entry.Value, "cache_creation_input_token_cost"));
            }
        }
        catch (JsonException)
        {
            return new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    private static double? Price(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number))
            return null;
        return double.IsFinite(number) && number >= 0 && number < MaxTokenPrice ? number : null;
    }

    /// <summary>Swaps in a new table and, when asked, writes the trimmed cache file.</summary>
    public void Replace(IReadOnlyDictionary<string, ModelPrice> entries, DateTimeOffset fetchedAt, bool persist = true)
    {
        var copy = new Dictionary<string, ModelPrice>(entries, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _prices = copy;
            FetchedAt = fetchedAt;
            _found.Clear();
        }

        if (persist)
            Save(copy, fetchedAt);
    }

    private void Save(Dictionary<string, ModelPrice> entries, DateTimeOffset fetchedAt)
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
                writer.WriteStartObject("models");
                foreach (var (key, price) in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject(key);
                    writer.WriteNumber("input", price.Input);
                    writer.WriteNumber("output", price.Output);
                    if (price.CacheRead is { } read)
                        writer.WriteNumber("cacheRead", read);
                    if (price.CacheCreation is { } creation)
                        writer.WriteNumber("cacheCreation", creation);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            var path = Path.Combine(directory, CacheFileName);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, stream.ToArray());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LogService.Shared.Log("WARN", "Model price cache could not be written: " + ex.GetType().Name);
        }
    }

    /// <summary>Loads the cache file written by the last download. A missing or damaged file leaves the
    /// list empty.</summary>
    public void Load()
    {
        LoadPrices();
        Fallback.Load();
    }

    private void LoadPrices()
    {
        try
        {
            var path = Path.Combine(_cacheDirectory(), CacheFileName);
            if (!File.Exists(path))
                return;

            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("fetchedAt", out var stamp) || stamp.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fetchedAt))
                return;

            var loaded = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in models.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var input = Price(entry.Value, "input");
                var output = Price(entry.Value, "output");
                if (input is null || output is null)
                    continue;
                loaded[entry.Name] = new ModelPrice(input.Value, output.Value, Price(entry.Value, "cacheRead"), Price(entry.Value, "cacheCreation"));
            }

            if (loaded.Count > 0)
                Replace(loaded, fetchedAt, persist: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
        {
            // A damaged cache means no prices until the next download.
        }
    }

    /// <summary>True when the model name carries the word "free" as its own token (<c>x/y:free</c>,
    /// <c>nemotron-3-ultra-free</c>); such a model costs nothing.</summary>
    internal static bool IsFree(string model) => FreeWord.IsMatch(model);

    /// <summary>The price of <paramref name="model"/>, or null when the list has none. A free model costs
    /// nothing once a list exists. An unknown model
    /// is reported once per run through the change log; an empty list reports nothing, since then no
    /// model is known.</summary>
    public ModelPrice? Find(string model)
    {
        Dictionary<string, ModelPrice> prices;
        IReadOnlyDictionary<string, List<FallbackPriceList.Entry>> fallback;
        lock (_gate)
        {
            if (_prices.Count == 0)
                return null;
            prices = _prices;
            fallback = _fallbackFrozen ?? Fallback.Current;
            if (_found.TryGetValue(model, out var cached))
                return cached;
        }

        var price = IsFree(model) ? FreePrice : Lookup(prices, model);
        if (price is null)
        {
            Fallback.MarkWanted();
            price = FallbackPriceList.Find(fallback, model);
            if (price is null && PlainAlias(model) is { } plain)
                price = FallbackPriceList.Find(fallback, plain);
        }

        var report = false;
        lock (_gate)
        {
            // Remember the answer only while both tables are still the ones it was found in.
            if (ReferenceEquals(prices, _prices) && ReferenceEquals(fallback, _fallbackFrozen ?? Fallback.Current))
                _found[model] = price;
            if (price is null && !KnownUnpriced.Contains(model))
                report = _reported.Add(model);
        }

        if (report)
            ReadChangeLog.Report("Pricing", $"no price known for model '{ReadChangeLog.SafeName(model)}'; its tokens are left out of the cost estimate.");
        return price;
    }

    private static ModelPrice? Lookup(Dictionary<string, ModelPrice> prices, string model)
    {
        var name = model.Trim();
        if (name.Length == 0)
            return null;

        if (Resolve(prices, name) is { } found)
            return found;

        // Last resort: a tool's own alias ("gemini-pro-default", "gemini-3-flash-a") is looked up as
        // the plain model it stands for.
        return PlainAlias(name) is { } plain ? Resolve(prices, plain) : null;
    }

    /// <summary>The model name without a tool's alias suffix, or null when it has none.</summary>
    private static string? PlainAlias(string name)
    {
        name = name.Trim();
        var plain = AliasSuffix.Replace(name, "");
        return plain.Length > 0 && plain.Length != name.Length ? plain : null;
    }

    private static ModelPrice? Resolve(Dictionary<string, ModelPrice> prices, string name)
    {
        if (Direct(prices, name) is { } direct)
            return direct;

        var stripped = DateSuffix.Replace(name, "");
        if (stripped.Length != name.Length && Direct(prices, stripped) is { } undated)
            return undated;

        return ByPrefix(prices, stripped);
    }

    private static ModelPrice? Direct(Dictionary<string, ModelPrice> prices, string name)
    {
        if (prices.TryGetValue(name, out var exact))
            return exact;
        foreach (var prefix in MakerPrefixes)
            if (prices.TryGetValue(prefix + name, out var prefixed))
                return prefixed;
        return null;
    }

    /// <summary>The list key whose name equals the model name or, failing that, the longest key that
    /// starts it (or the shortest key it starts), within the same maker family so a name never borrows
    /// another maker's price. Among keys with the same name the one filed under the model's own maker
    /// wins, then the shortest key, then the first in ordinal order.</summary>
    private static ModelPrice? ByPrefix(Dictionary<string, ModelPrice> prices, string name)
    {
        var lowered = name.ToLowerInvariant();
        var family = Family(lowered);
        Candidate? equal = null, longest = null, shortest = null;

        foreach (var (key, price) in prices)
        {
            var lowKey = key.ToLowerInvariant();
            var bare = StripMaker(lowKey);
            if (Family(bare) != family)
                continue;

            var candidate = new Candidate(price, bare, lowKey, string.Equals(MakerOf(lowKey), family, StringComparison.Ordinal));
            if (string.Equals(bare, lowered, StringComparison.Ordinal))
            {
                if (equal is null || candidate.Beats(equal, 0))
                    equal = candidate;
            }
            else if (bare.Length < lowered.Length && lowered.StartsWith(bare, StringComparison.Ordinal) && IsBoundary(lowered[bare.Length]))
            {
                if (longest is null || candidate.Beats(longest, -1))
                    longest = candidate;
            }
            else if (bare.Length > lowered.Length && bare.StartsWith(lowered, StringComparison.Ordinal) && IsBoundary(bare[lowered.Length]))
            {
                if (shortest is null || candidate.Beats(shortest, 1))
                    shortest = candidate;
            }
        }

        return (equal ?? longest ?? shortest)?.Price;
    }

    /// <summary>A list key that fits a model name. <see cref="Beats"/> orders two of them: by name length
    /// (<c>direction</c> 1 = shorter first, -1 = longer first, 0 = ignore), then the name, then the key
    /// under the model's own maker, then the shorter key, then the key itself.</summary>
    private sealed record Candidate(ModelPrice Price, string Bare, string Key, bool Own)
    {
        public bool Beats(Candidate other, int direction)
        {
            if (direction != 0 && Bare.Length != other.Bare.Length)
                return direction > 0 ? Bare.Length < other.Bare.Length : Bare.Length > other.Bare.Length;
            var byName = string.CompareOrdinal(Bare, other.Bare);
            if (byName != 0)
                return byName < 0;
            if (Own != other.Own)
                return Own;
            return Key.Length != other.Key.Length ? Key.Length < other.Key.Length : string.CompareOrdinal(Key, other.Key) < 0;
        }
    }

    private static bool IsBoundary(char c) => c is '-' or '.' or ':' or '@' or '_';

    private static string StripMaker(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash >= 0 ? key[(slash + 1)..] : key;
    }

    /// <summary>The path part just before the model name in a list key ("minimax" in
    /// "minimax/minimax-m3"); empty when the key has no path.</summary>
    private static string MakerOf(string key)
    {
        var slash = key.LastIndexOf('/');
        if (slash < 0)
            return "";
        var before = key[..slash];
        return before[(before.LastIndexOf('/') + 1)..];
    }

    /// <summary>The maker a model name belongs to, from its first word.</summary>
    internal static string Family(string name)
    {
        var end = name.IndexOfAny(['-', '.', ':', '_', '@']);
        var word = end < 0 ? name : name[..end];
        if (word.StartsWith("gpt", StringComparison.Ordinal) || word.StartsWith("codex", StringComparison.Ordinal)
            || (word.Length >= 2 && word[0] == 'o' && char.IsAsciiDigit(word[1])) || word.StartsWith("chatgpt", StringComparison.Ordinal))
            return "openai";
        return word;
    }
}
