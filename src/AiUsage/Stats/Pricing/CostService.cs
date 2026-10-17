using System.Globalization;
using System.Net.Http;
using System.Text;
using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Stats.Pricing;

/// <summary>The two downloads behind the cost estimate. Each goes through the app's one allow-listed
/// downloader, so the host is fixed, only https is used, redirects are checked per hop and the body is
/// capped.</summary>
internal static class PriceDownloader
{
    internal const string PriceListUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
    internal const string PriceListHost = "raw.githubusercontent.com";
    internal const long PriceListMaxBytes = 16L * 1024 * 1024;

    internal const string RateUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    internal const string RateHost = "www.ecb.europa.eu";
    internal const long RateMaxBytes = 1L * 1024 * 1024;

    /// <summary>Downloads <paramref name="url"/> to a temporary file in <paramref name="directory"/>,
    /// reads it as text and removes the file. Null on any failure (one log line says which host).</summary>
    internal static async Task<string?> FetchTextAsync(
        string url, string host, long maxBytes, string directory, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        string? path = null;
        try
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "download-" + Guid.NewGuid().ToString("N") + ".tmp");
            await AllowListedDownloader.DownloadAsync(
                url, path, uri => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase), handler,
                AllowListedDownloader.StallTimeout, AllowListedDownloader.OverallTimeout, maxBytes, null, ct);
            return await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException
            or UnauthorizedAccessException or OperationCanceledException or UriFormatException or NotSupportedException)
        {
            LogService.Shared.Log("WARN", $"Download from {host} failed: {ex.GetType().Name}");
            return null;
        }
        finally
        {
            try
            {
                if (path is not null && File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary file is harmless.
            }
        }
    }
}

/// <summary>
/// What the statistics window asks for the cost estimate: the figure for a set of rows, the tooltip
/// lines around it, and the background refresh of the price list and the euro rate. Everything is off
/// when the setting is off: no download, no text.
/// </summary>
public sealed class CostService
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    private readonly Func<bool> _enabled;
    private readonly Func<CostCurrency> _currency;
    private readonly Func<CultureInfo> _culture;
    private readonly Func<RegionInfo> _region;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<bool> _networkAllowed;
    private readonly string _cacheDirectory;
    private readonly HttpMessageHandler? _handler;
    private DateTimeOffset _priceAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _rateAttempt = DateTimeOffset.MinValue;
    private int _refreshing;

    internal CostService(
        Func<bool> enabled, Func<CostCurrency> currency, string? cacheDirectory = null, Func<CultureInfo>? culture = null,
        Func<RegionInfo>? region = null, Func<DateTimeOffset>? now = null, Func<bool>? networkAllowed = null,
        HttpMessageHandler? handler = null)
    {
        _enabled = enabled;
        _currency = currency;
        _cacheDirectory = cacheDirectory ?? StartupProfile.CacheDirectory;
        _culture = culture ?? (() => CultureInfo.CurrentCulture);
        _region = region ?? (() => RegionInfo.CurrentRegion);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _networkAllowed = networkAllowed ?? (() => !FetchContext.NetworkHeld);
        _handler = handler;
        Prices = new ModelPriceList(() => _cacheDirectory);
        Rate = new EurRate(() => _cacheDirectory);
        Prices.Load();
        Rate.Load();
    }

    public ModelPriceList Prices { get; }

    public EurRate Rate { get; }

    /// <summary>Raised on a background thread after a download changed what the figures say.</summary>
    public event Action? Changed;

    public bool Enabled => _enabled();

    /// <summary>The currency the figures are shown in right now.</summary>
    public CostCurrency EffectiveCurrency => CostFormatter.Effective(_currency(), _region());

    /// <summary>The cost of <paramref name="records"/>; <see cref="CostSummary.Empty"/> when the
    /// estimate is off or no prices are loaded.</summary>
    public CostSummary Sum(IEnumerable<StatsRecord> records) =>
        Enabled && !Prices.IsEmpty ? CostCalculator.Sum(records, Prices) : CostSummary.Empty;

    /// <summary>The text for a cost figure: empty when off or nothing to say.</summary>
    public string Text(CostSummary summary) =>
        Enabled ? CostFormatter.Format(summary, EffectiveCurrency, Rate.UsdPerEur, _culture()) : "";

    public string Text(IEnumerable<StatsRecord> records) => Text(Sum(records));

    /// <summary>The "no price known for ..." tooltip line, or null when every model is priced.</summary>
    public static string? UnpricedLine(CostSummary summary) => summary.HasUnpriced
        ? LocalizationService.Instance.Format("Stats.Cost.Unpriced", string.Join(", ", summary.UnpricedModels))
        : null;

    /// <summary>The explanation that goes under a cost figure: where the numbers come from and as of
    /// when, or that the prices are not loaded yet.</summary>
    public string Explanation()
    {
        var loc = LocalizationService.Instance;
        return Prices.FetchedAt is { } at && !Prices.IsEmpty
            ? loc.Format("Stats.Cost.Tip", at.ToLocalTime().ToString("d", _culture()))
            : loc["Stats.Cost.NoPrices"];
    }

    /// <summary>A cost figure as dollars for the export: invariant, two to six decimals; empty when no
    /// price is known.</summary>
    public static string UsdForExport(CostSummary summary) =>
        summary.AnyPriced ? summary.Usd.ToString("0.######", CultureInfo.InvariantCulture) : "";

    /// <summary>Called after the switch or the currency changed: open statistics redraw, then whatever
    /// is now needed (the rate after a change to euro) is loaded in the background.</summary>
    internal void NotifySettingsChanged()
    {
        Changed?.Invoke();
        if (Enabled)
            _ = RefreshAsync();
    }

    /// <summary>Loads what is stale: the price list, and the euro rate when euro is shown. Does nothing
    /// when the estimate is off or the network is held back. A failed download keeps the old cache and
    /// is tried again after an hour. Safe to call often.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!Enabled || !_networkAllowed() || Interlocked.Exchange(ref _refreshing, 1) == 1)
            return;

        try
        {
            var changed = false;
            var now = _now();
            if (Prices.IsStale(now) && now - _priceAttempt >= RetryAfterFailure)
            {
                _priceAttempt = now;
                var text = await PriceDownloader.FetchTextAsync(
                    PriceDownloader.PriceListUrl, PriceDownloader.PriceListHost, PriceDownloader.PriceListMaxBytes, _cacheDirectory, ct, _handler);
                var parsed = text is null ? null : ModelPriceList.Parse(text);
                if (parsed is { Count: > 0 })
                {
                    Prices.Replace(parsed, now);
                    _priceAttempt = DateTimeOffset.MinValue;
                    changed = true;
                }
                else if (text is not null)
                {
                    LogService.Shared.Log("WARN", "The downloaded price list held no usable prices; the earlier one stays.");
                }
            }

            if (EffectiveCurrency == CostCurrency.Eur && Rate.IsStale(now) && now - _rateAttempt >= RetryAfterFailure)
            {
                _rateAttempt = now;
                var xml = await PriceDownloader.FetchTextAsync(
                    PriceDownloader.RateUrl, PriceDownloader.RateHost, PriceDownloader.RateMaxBytes, _cacheDirectory, ct, _handler);
                if (xml is not null && EurRate.ParseEcb(xml) is { } rate)
                {
                    Rate.Set(rate, now);
                    _rateAttempt = DateTimeOffset.MinValue;
                    changed = true;
                }
                else if (xml is not null)
                {
                    LogService.Shared.Log("WARN", "The downloaded euro rate file held no usable rate; the earlier one stays.");
                }
            }

            if (changed)
                Changed?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }
}
