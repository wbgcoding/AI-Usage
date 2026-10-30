using System.Globalization;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats.Pricing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.Stats;

/// <summary>The plan forecast section: the numbers are computed on a pool thread from the quota
/// history read for the limits section and kept until the records, the history, the plans or the
/// cost inputs change; the rows are drawn from them on the UI thread.</summary>
public sealed partial class StatsViewModel
{
    /// <summary>The account keys of a provider (<c>claude</c>, <c>claude#2</c>) - a seam the window fills
    /// from the settings. Called on a pool thread; a failure falls back to the provider's own key.</summary>
    internal Func<string, IReadOnlyList<string>> AccountKeysOf { get; set; } = id => [id];

    /// <summary>The normalized plan of an account (<c>Max 5x</c>) from the live tiles; null when unknown.
    /// Called on the UI thread.</summary>
    internal Func<string, string?> PlanOf { get; set; } = _ => null;

    /// <summary>One account's quota history of the lookback.</summary>
    private sealed record AccountHistory(string Key, IReadOnlyList<HistoryPoint> Points);

    /// <summary>What a forecast was computed from, and the outcome.</summary>
    private sealed record ForecastState(
        IReadOnlyList<StatsRecord> Records, LimitInputs Inputs, string Signature,
        IReadOnlyDictionary<string, ProviderForecast> Forecasts, StatsCostView Cost);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForecast))]
    private IReadOnlyList<StatsForecastGroupViewModel> forecastGroups = [];

    /// <summary>True when at least one shown provider has a forecast; the section is hidden otherwise.</summary>
    public bool HasForecast => ForecastGroups.Count > 0;

    private ForecastState? _forecastState;
    private int _forecastGeneration;
    private Task _forecastRefresh = Task.CompletedTask;

    /// <summary>The forecast computation started by the last limits redraw, for a test to wait on.</summary>
    internal Task ForecastRefreshForTest => _forecastRefresh;

    /// <summary>The quota history of every account of the indexed providers, for the forecast.</summary>
    private Dictionary<string, IReadOnlyList<AccountHistory>> LoadAccountHistory(
        Func<string, DateTimeOffset, DateTimeOffset, IReadOnlyList<HistoryPoint>> load, DateTimeOffset now,
        Dictionary<string, IReadOnlyList<HistoryPoint>> alreadyLoaded)
    {
        var since = now.AddDays(-StatsTokensPerPercent.LookbackDays);
        var result = new Dictionary<string, IReadOnlyList<AccountHistory>>();
        foreach (var id in ProviderCoverage.IndexedProviderIds)
        {
            IReadOnlyList<string> keys;
            try
            {
                keys = AccountKeysOf(id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // The settings were changed while they were read; the provider's own history still counts.
                keys = [];
            }

            if (keys.Count == 0)
                keys = [id];
            result[id] = [.. keys.Select(key => new AccountHistory(
                key, key == id && alreadyLoaded.TryGetValue(id, out var held) ? held : load(key, since, now)))];
        }

        return result;
    }

    /// <summary>Draws the forecast section, computing the numbers first when what they depend on changed.</summary>
    private void RefreshForecast(LimitInputs held, LocalizationService loc)
    {
        var cost = new StatsCostView(_cost);
        var plans = held.Accounts.Values.SelectMany(accounts => accounts).ToDictionary(account => account.Key, account => PlanOf(account.Key));
        var signature = string.Join('|', plans.Select(pair => pair.Key + "=" + pair.Value))
            + "|" + cost.Active + "|" + cost.Text(new CostSummary(1000, true, [])) + "|" + _cost?.Prices.FetchedAt;
        var records = _sourceRecords;
        if (_forecastState is { } state && ReferenceEquals(state.Records, records) && ReferenceEquals(state.Inputs, held) && state.Signature == signature)
        {
            ForecastGroups = BuildForecastGroups(state, loc);
            return;
        }

        var generation = ++_forecastGeneration;
        var previous = _forecastState;
        _forecastRefresh = ComputeForecastAsync(records, held, plans, signature, cost, previous, generation);
    }

    private async Task ComputeForecastAsync(
        IReadOnlyList<StatsRecord> records, LimitInputs held, IReadOnlyDictionary<string, string?> plans, string signature,
        StatsCostView cost, ForecastState? previous, int generation)
    {
        try
        {
            var state = await Task.Run(() =>
            {
                if (previous is not null && ReferenceEquals(previous.Records, records) && previous.Signature == signature
                    && previous.Inputs.SameAccountsAs(held))
                {
                    return previous with { Inputs = held };
                }

                return new ForecastState(records, held, signature, ComputeForecasts(records, held, plans, cost), cost);
            });
            if (generation != _forecastGeneration)
                return;

            _forecastState = state;
            ForecastGroups = BuildForecastGroups(state, LocalizationService.Instance);
        }
        catch (Exception ex)
        {
            // Nothing awaits this task: a failed computation leaves the section as it was.
            LogService.Shared.LogError($"Stats plan forecast could not be computed ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private static Dictionary<string, ProviderForecast> ComputeForecasts(
        IReadOnlyList<StatsRecord> records, LimitInputs held, IReadOnlyDictionary<string, string?> plans, StatsCostView cost)
    {
        var since = DateOnly.FromDateTime(DateTime.Now.AddDays(-StatsTokensPerPercent.LookbackDays));
        var forecasts = new Dictionary<string, ProviderForecast>();
        Func<IReadOnlyList<StatsRecord>, CostSummary>? pricer = cost.Active ? rows => cost.Sum(rows) : null;
        foreach (var (providerId, accounts) in held.Accounts)
        {
            var providerRecords = records.Where(record => record.Provider == providerId && record.Day >= since).ToList();
            if (providerRecords.Count == 0)
                continue;

            var forecast = StatsPlanForecast.Build(
                providerId, [.. accounts.Select(account => new ForecastAccount(account.Key, plans.GetValueOrDefault(account.Key), account.Points))],
                providerRecords, pricer, DateTimeOffset.Now, TimeZoneInfo.Local);
            if (forecast is not null)
                forecasts[providerId] = forecast;
        }

        return forecasts;
    }

    private List<StatsForecastGroupViewModel> BuildForecastGroups(ForecastState state, LocalizationService loc)
    {
        var groups = new List<StatsForecastGroupViewModel>();
        foreach (var providerId in ProviderCoverage.IndexedProviderIds)
        {
            if (!state.Forecasts.TryGetValue(providerId, out var forecast) || (HasProviderFilter && !_selectedProviders.Contains(providerId)))
                continue;

            groups.Add(new StatsForecastGroupViewModel(providerId, ForecastHeading(forecast, loc), ForecastRows(forecast, state.Cost, loc)));
        }

        return groups;
    }

    private static string ForecastHeading(ProviderForecast forecast, LocalizationService loc)
    {
        var heading = ProviderDisplayNames.GetValueOrDefault(forecast.ProviderId, forecast.ProviderId);
        if (forecast.Plans.Count > 0)
            heading += " · " + string.Join(" + ", forecast.Plans);
        if (forecast.AccountCount > 1)
            heading += " (" + loc.Format("Stats.Forecast.Accounts", forecast.AccountCount) + ")";
        return heading;
    }

    private static List<StatsForecastRowViewModel> ForecastRows(ProviderForecast forecast, StatsCostView cost, LocalizationService loc)
    {
        var rangeNote = loc["Tip.Stats.Forecast"];
        var rows = new List<StatsForecastRowViewModel>();
        foreach (var (window, key) in new[] { (forecast.FiveHour, "Stats.Forecast.FiveHour"), (forecast.Weekly, "Stats.Forecast.Week") })
        {
            if (window is null || window.Result.Status == PerPercentStatus.TooFew)
                continue;

            var text = loc.Format(key, ShortenTokens(window.CapacityTokens, loc));
            if (window.ApiUsdPerWindow is { } api && Money(cost, api) is { Length: > 0 } money)
                text += " · " + loc.Format("Stats.Forecast.ApiValue", money);

            var detail = window.Result.Status == PerPercentStatus.PerModel
                // Each figure is what that model alone would fill the window with, so it can exceed the
                // mixed total above; the label says so.
                ? loc.Format("Stats.Forecast.ModelsAlone", string.Join(" · ", window.Result.Items.Select(item => loc.Format(
                    "Stats.PerPercent.Item", ShortenTokens(item.TokensPerPercent * 100, loc), item.IsOther ? loc["Stats.Other"] : item.Model))))
                : loc["Stats.Forecast.AllModels"];
            var cached = window.Result.CacheReadPerPercent > 0 ? ShortenTokens(window.Result.CacheReadPerPercent, loc) : "–";
            var lines = new List<string> { loc.Format("Stats.PerPercent.Tip", window.Result.IntervalCount, cached) };
            if (window.UnpricedModels.Count > 0 && CostService.UnpricedLine(new CostSummary(0, true, window.UnpricedModels)) is { } unpriced)
                lines.Add(unpriced);
            lines.Add(rangeNote);
            rows.Add(new StatsForecastRowViewModel(text, detail, string.Join('\n', lines)));
        }

        var tooFew = (forecast.FiveHour is null || forecast.FiveHour.Result.Status == PerPercentStatus.TooFew)
            && (forecast.Weekly is null || forecast.Weekly.Result.Status == PerPercentStatus.TooFew);
        if (tooFew)
            rows.Add(new StatsForecastRowViewModel(loc["Stats.Forecast.TooFew"], "", rangeNote));

        var averages = new List<string>();
        if (forecast.FiveHour?.Average is { } five)
            averages.Add(loc.Format("Stats.Forecast.Average.FiveHour", ShortenTokens(five.AverageTokens, loc), FormatLimitPercent(five.AveragePeak)));
        if (forecast.Weekly?.Average is { } week)
            averages.Add(loc.Format("Stats.Forecast.Average.Week", ShortenTokens(week.AverageTokens, loc), FormatLimitPercent(week.AveragePeak)));
        if (averages.Count > 0)
            rows.Add(new StatsForecastRowViewModel(
                loc.Format("Stats.Forecast.Average", string.Join(" · ", averages)), "", loc["Tip.Stats.Forecast.Average"] + "\n" + rangeNote));

        if (forecast.MonthlyApiUsd is { } monthly && Money(cost, monthly) is { Length: > 0 } monthlyText)
        {
            var parts = new List<string> { loc.Format("Stats.Forecast.ApiValue", monthlyText) };
            if (forecast.MonthlyPlanUsd is { } plan && Money(cost, plan) is { Length: > 0 } planText)
                parts.Add(loc.Format("Stats.Forecast.Plan", planText));
            if (forecast.Ratio is { } ratio)
                parts.Add(ratio.ToString("0.0", CultureInfo.CurrentCulture) + "x");
            rows.Add(new StatsForecastRowViewModel(
                loc.Format("Stats.Forecast.Month", string.Join(" · ", parts)), "", loc["Tip.Stats.Forecast.Month"] + "\n" + rangeNote));
        }

        return rows;
    }

    /// <summary>A dollar amount in the cost currency without the estimate tilde (the rows carry their own "≈"); empty when costs are off.</summary>
    private static string Money(StatsCostView cost, double usd) =>
        cost.Active ? cost.Text(new CostSummary(usd, true, [])).TrimStart('~') : "";
}
