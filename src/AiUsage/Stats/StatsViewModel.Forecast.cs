using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats.Pricing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.Stats;

/// <summary>The plan forecast section: the numbers are computed on a pool thread from the quota
/// history read for the limits section and kept until the records, the history, the plans or the
/// cost inputs change; the cards are drawn from them on the UI thread.</summary>
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
    private IReadOnlyList<StatsForecastProviderViewModel> forecastProviders = [];

    /// <summary>True when at least one shown provider has a forecast; the section is hidden otherwise.</summary>
    public bool HasForecast => ForecastProviders.Count > 0;

    /// <summary>The provider buttons above the forecast, one per provider with a forecast.</summary>
    public ObservableCollection<StatsForecastButtonViewModel> ForecastButtons { get; } = [];

    /// <summary>The buttons are only worth showing when there is a choice.</summary>
    public bool HasForecastChoice => ForecastButtons.Count > 1;

    /// <summary>With one provider only, a compact name and plan line stands in for the button row.</summary>
    public bool HasSingleForecast => ForecastButtons.Count == 1;

    /// <summary>The provider the user picked in the forecast section; empty until one is picked, and
    /// again once it has no forecast. Kept only while the window is open.</summary>
    private string _pickedForecastProvider = "";

    /// <summary>The provider the forecast section shows: the picked one, else the one provider of the
    /// provider filter, else the first with a forecast. Empty without any forecast.</summary>
    public string SelectedForecastProvider { get; private set; } = "";

    /// <summary>The one provider block the forecast section shows; none without a forecast.</summary>
    public IReadOnlyList<StatsForecastProviderViewModel> ShownForecast { get; private set; } = [];

    partial void OnForecastProvidersChanged(IReadOnlyList<StatsForecastProviderViewModel> value) => ResolveForecastProvider();

    private void PickForecastProvider(string providerId)
    {
        _pickedForecastProvider = providerId;
        ResolveForecastProvider();
    }

    /// <summary>Settles which provider the forecast section shows and refreshes the buttons and the block.</summary>
    private void ResolveForecastProvider()
    {
        var ids = ForecastProviders.Select(provider => provider.ProviderId).ToList();
        if (!ids.Contains(_pickedForecastProvider))
            _pickedForecastProvider = "";

        SelectedForecastProvider = _pickedForecastProvider.Length > 0 ? _pickedForecastProvider
            : _selectedProviders.Count == 1 && ids.Contains(_selectedProviders[0]) ? _selectedProviders[0]
            : ids.FirstOrDefault() ?? "";
        ShownForecast = [.. ForecastProviders.Where(provider => provider.ProviderId == SelectedForecastProvider)];

        if (!ForecastButtons.Select(button => button.ProviderId).SequenceEqual(ids))
        {
            ForecastButtons.Clear();
            foreach (var provider in ForecastProviders)
                ForecastButtons.Add(new StatsForecastButtonViewModel(provider.ProviderId, provider.Name, provider.Brush, PickForecastProvider));
        }

        foreach (var button in ForecastButtons)
        {
            button.Mark(button.ProviderId == SelectedForecastProvider);
            button.SetDetail(ForecastProviders.FirstOrDefault(provider => provider.ProviderId == button.ProviderId)?.Detail ?? "");
        }

        OnPropertyChanged(nameof(SelectedForecastProvider));
        OnPropertyChanged(nameof(ShownForecast));
        OnPropertyChanged(nameof(HasForecastChoice));
        OnPropertyChanged(nameof(HasSingleForecast));
    }

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
            ForecastProviders = BuildForecastProviders(state, loc);
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
            ForecastProviders = BuildForecastProviders(state, LocalizationService.Instance);
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
            // Models of another vendor (a tool pointed at a foreign API) were not paid for by this provider's quota.
            var providerRecords = ProviderModelFilter.Own(providerId, records.Where(record => record.Provider == providerId && record.Day >= since));
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

    private List<StatsForecastProviderViewModel> BuildForecastProviders(ForecastState state, LocalizationService loc)
    {
        var providers = new List<StatsForecastProviderViewModel>();
        foreach (var providerId in ProviderCoverage.IndexedProviderIds)
        {
            if (!state.Forecasts.TryGetValue(providerId, out var forecast) || (HasProviderFilter && !_selectedProviders.Contains(providerId)))
                continue;

            providers.Add(BuildForecastProvider(forecast, state.Cost, loc));
        }

        return providers;
    }

    internal static StatsForecastProviderViewModel BuildForecastProvider(ProviderForecast forecast, StatsCostView cost, LocalizationService loc)
    {
        var brush = new SolidColorBrush(ChartPalette.ForProvider(forecast.ProviderId));
        brush.Freeze();
        var rangeNote = loc["Tip.Stats.Forecast"];
        var windows = new List<StatsForecastWindowViewModel>();
        if (forecast.FiveHour is { } fiveHour)
            windows.Add(ForecastWindowCard(fiveHour, loc["Stats.Forecast.Window.FiveHour"], cost, loc, rangeNote, brush));
        if (forecast.Weekly is { } weekly)
            windows.Add(ForecastWindowCard(weekly, loc["Stats.Forecast.Window.Week"], cost, loc, rangeNote, brush));

        return new StatsForecastProviderViewModel(
            forecast.ProviderId, ProviderDisplayNames.GetValueOrDefault(forecast.ProviderId, forecast.ProviderId), brush,
            string.Join(" + ", forecast.Plans),
            forecast.AccountCount > 1 ? loc.Format("Stats.Forecast.Accounts", forecast.AccountCount) : "",
            windows, ForecastMonthCard(forecast, cost, loc, rangeNote, brush));
    }

    private static StatsForecastWindowViewModel ForecastWindowCard(
        ForecastWindow window, string caption, StatsCostView cost, LocalizationService loc, string rangeNote, Brush brush)
    {
        if (window.Result.Status == PerPercentStatus.TooFew)
            return new StatsForecastWindowViewModel(caption, false, loc["Stats.Forecast.TooFew"], "", 0, "", "", [], rangeNote, "", brush);

        var api = window.ApiUsdPerWindow is { } usd && Money(cost, usd) is { Length: > 0 } money ? loc.Format("Stats.Forecast.ApiCard", money) : "";
        var average = "";
        var averageFraction = 0.0;
        if (window.Average is { } found)
        {
            // The average is estimated from hours the limit was read for. When a period still cost more per
            // percent than the whole fit, its tokens can exceed the full-window capacity; the line then shows
            // the share of that capacity the tokens make up instead of a smaller measured peak.
            var percent = window.CapacityTokens > 0 && found.AverageTokens > window.CapacityTokens
                ? (double)found.AverageTokens / window.CapacityTokens * 100
                : found.AveragePeak;
            average = loc.Format("Stats.Forecast.AverageLine", ShortenTokens(found.AverageTokens, loc), FormatLimitPercent(percent));
            averageFraction = Math.Clamp(percent / 100, 0, 1);
        }

        // Each bar is what that model alone would fill the window with, so one can exceed the mixed
        // total above; the lengths are relative to the longest of them.
        var models = new List<StatsForecastModelBar>();
        if (window.Result.Status == PerPercentStatus.PerModel && window.Result.Items.Count > 0)
        {
            var longest = window.Result.Items.Max(item => item.TokensPerPercent);
            foreach (var item in window.Result.Items)
            {
                var name = item.IsOther ? loc["Stats.Other"] : item.Model;
                models.Add(new StatsForecastModelBar(
                    $"{name} · {ShortenTokens(item.TokensPerPercent * 100, loc)}",
                    longest > 0 ? (double)item.TokensPerPercent / longest : 0));
            }
        }

        var cached = window.Result.CacheReadPerPercent > 0 ? ShortenTokens(window.Result.CacheReadPerPercent, loc) : "–";
        var lines = new List<string> { loc.Format("Stats.PerPercent.Tip", window.Result.IntervalCount, cached) };
        if (window.UnpricedModels.Count > 0 && CostService.UnpricedLine(new CostSummary(0, true, window.UnpricedModels)) is { } unpriced)
            lines.Add(unpriced);
        lines.Add(rangeNote);
        return new StatsForecastWindowViewModel(
            caption, true, loc.Format("Stats.Forecast.Capacity", ShortenTokens(window.CapacityTokens, loc)), api,
            averageFraction, average, models.Count > 0 ? loc["Stats.Forecast.ModelsCaption"] : loc["Stats.Forecast.AllModels"], models,
            string.Join('\n', lines), loc["Tip.Stats.Forecast.Average"] + "\n" + rangeNote, brush);
    }

    private static StatsForecastMonthViewModel? ForecastMonthCard(ProviderForecast forecast, StatsCostView cost, LocalizationService loc, string rangeNote, Brush brush)
    {
        if (forecast.MonthlyApiUsd is not { } monthly || Money(cost, monthly) is not { Length: > 0 } monthlyText)
            return null;

        // The plan price in the shown currency: the euro list price where one is published, else the
        // dollar price (converted at the day's rate when euros show). The ratio uses the same two figures.
        var planText = "";
        var planTip = "";
        string ratio = "";
        if (forecast.PlanPrices is { Count: > 0 } prices)
        {
            var euroList = cost.Euro && prices.All(price => price.Eur is not null);
            var price = euroList
                ? PlanPriceText(prices.Sum(price => price.Eur!.Value), "€")
                : Money(cost, prices.Sum(price => price.Usd));
            var planUsd = euroList ? prices.Sum(price => price.Eur!.Value) * cost.UsdPerEur!.Value : prices.Sum(price => price.Usd);
            planText = $"{string.Join(" + ", prices.Select(price => price.Plan))} {price}";
            var lines = prices.DistinctBy(price => price.Plan).Select(price => loc.Format("Tip.Stats.Forecast.PlanPrice",
                price.Plan, PlanPriceText(price.Usd, "$"), price.Eur is { } eur ? PlanPriceText(eur, "€") : "–",
                price.Source, price.CheckedOn.ToString("d", CultureInfo.CurrentCulture))).ToList();
            lines.Add(loc["Tip.Stats.Forecast.PlanPriceNote"]);
            if (cost.Euro && !euroList)
                lines.Add(loc["Tip.Stats.Forecast.PlanPriceConverted"]);
            planTip = string.Join('\n', lines);
            if (planUsd > 0)
                ratio = (monthly / planUsd).ToString("0.0", CultureInfo.CurrentCulture) + "×";
        }

        return new StatsForecastMonthViewModel(
            loc.Format("Stats.Forecast.MonthApi", monthlyText), planText, planTip,
            ratio, ratio.Length > 0 ? loc["Stats.Forecast.RatioCaption"] : "",
            loc["Tip.Stats.Forecast.Month"] + "\n" + rangeNote, brush);
    }

    /// <summary>A list price as it is published: whole amounts without cents, else with two decimals.</summary>
    private static string PlanPriceText(double amount, string symbol)
    {
        var numbers = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
        numbers.CurrencySymbol = symbol;
        return amount.ToString(Math.Abs(amount * 100 % 100) < 0.5 ? "C0" : "C2", numbers).Replace(' ', '\u00a0');
    }

    /// <summary>A dollar amount in the cost currency without the estimate tilde (the texts carry their own "≈"); empty when costs are off.</summary>
    private static string Money(StatsCostView cost, double usd) =>
        cost.Active ? cost.Text(new CostSummary(usd, true, [])).TrimStart('~') : "";
}
