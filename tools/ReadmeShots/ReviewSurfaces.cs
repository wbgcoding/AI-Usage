// The review mode's list of surfaces and its command line: every picture the review set can draw is
// one entry here, picked by id or by group, and rendered per theme, language and size.

using System.IO;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.ViewModels;

namespace ReadmeShots;

/// <summary>How wide a surface is drawn: its smallest allowed width, or its normal one. The height is
/// always measured from the content.</summary>
internal enum SizeKind { Min, Default }

/// <summary>One drawable thing. <paramref name="HasMinSize"/> says whether it has a smaller variant;
/// surfaces without one are drawn once, as <see cref="SizeKind.Default"/>.</summary>
internal sealed record Surface(string Id, string Group, Action<SurfaceContext> Render, bool HasMinSize = false);

/// <summary>Everything a surface needs for one picture: the theme and language the loop has applied
/// (as file name parts), the size to draw, the output and data folders and the shared sample data.</summary>
internal sealed class SurfaceContext
{
    private readonly int[] _counter;

    internal SurfaceContext(
        string themeName, string language, SizeKind size, string outputDirectory, string dataDirectory,
        IReadOnlyList<SampleProviders.Sample> samples, IReadOnlyList<StatsRecord> records, DateOnly busyDay,
        DateTimeOffset now, int[] counter)
    {
        ThemeName = themeName;
        Language = language;
        Size = size;
        OutputDirectory = outputDirectory;
        DataDirectory = dataDirectory;
        Samples = samples;
        Records = records;
        BusyDay = busyDay;
        Now = now;
        _counter = counter;
    }

    internal string ThemeName { get; }

    internal string Language { get; }

    internal SizeKind Size { get; }

    internal string OutputDirectory { get; }

    internal string DataDirectory { get; }

    internal IReadOnlyList<SampleProviders.Sample> Samples { get; }

    internal IReadOnlyList<StatsRecord> Records { get; }

    internal DateOnly BusyDay { get; }

    internal DateTimeOffset Now { get; }

    internal IReadOnlyList<string> AllProviders => AppSettings.KnownProviderIds;

    /// <summary>A fresh data subfolder, so two surfaces never share a stats index.</summary>
    internal string NextDataFolder(string prefix) => Path.Combine(DataDirectory, prefix + "-" + _counter[0]++);

    /// <summary>The picture's file: <c>surface-theme-language-size.png</c>, dots of the id kept.</summary>
    internal string OutputPath(string id) =>
        Path.Combine(OutputDirectory, $"{id}-{ThemeName}-{Language}-{Size.ToString().ToLowerInvariant()}.png");

    /// <summary>The width for the size kind being drawn.</summary>
    internal double Pick(double defaultWidth, double minWidth) => Size == SizeKind.Min ? minWidth : defaultWidth;
}

internal static class ReviewSurfaces
{
    internal const double WidgetWidth = 380;
    internal const double WideTileWidth = 900;
    internal const double MinWidgetWidth = WindowPlacementService.MinWindowWidth;

    // HighContrast is not a pickable theme: it is what any theme resolves to while Windows
    // high-contrast mode is on, so it is rendered by faking that flag for the swap.
    internal static readonly (string Name, AppTheme Theme, bool HighContrast)[] Themes =
    [
        ("Dark", AppTheme.Dark, false), ("Light", AppTheme.Light, false),
        ("Nebula", AppTheme.Nebula, false), ("Terminal", AppTheme.Terminal, false),
        ("HighContrast", AppTheme.Dark, true),
    ];

    internal const string GroupWidget = "@widget";
    internal const string GroupStats = "@stats";
    internal const string GroupSettings = "@settings";
    internal const string GroupTiles = "@tiles";
    internal const string GroupDialogs = "@dialogs";
    internal const string GroupMenus = "@menus";

    internal static readonly string[] Groups =
        [GroupWidget, GroupStats, GroupSettings, GroupTiles, GroupDialogs, GroupMenus];

    /// <summary>The whole list, in the order a full run draws it. The settings pages come from the
    /// view model's own category list.</summary>
    internal static IReadOnlyList<Surface> Build(IReadOnlyList<string> settingsCategories)
    {
        var list = new List<Surface>
        {
            new("widget.full", GroupWidget, context => Program.RenderWidget(
                context.DataDirectory, context.Samples, context.Now, context.AllProviders, TileDensity.Full,
                context.OutputPath("widget.full"), context.Pick(WidgetWidth, MinWidgetWidth)), HasMinSize: true),
            new("widget.mini", GroupWidget, context => Program.RenderWidget(
                context.DataDirectory, context.Samples, context.Now, context.AllProviders, TileDensity.Mini,
                context.OutputPath("widget.mini"), context.Pick(WidgetWidth, MinWidgetWidth)), HasMinSize: true),
            new("widget.full.zoom90", GroupWidget, context => ZoomedWidget(context, "widget.full.zoom90", TileDensity.Full, 90), HasMinSize: true),
            new("widget.mini.zoom90", GroupWidget, context => ZoomedWidget(context, "widget.mini.zoom90", TileDensity.Mini, 90), HasMinSize: true),
            new("widget.full.zoom125", GroupWidget, context => ZoomedWidget(context, "widget.full.zoom125", TileDensity.Full, 125), HasMinSize: true),
            new("widget.mini.zoom125", GroupWidget, context => ZoomedWidget(context, "widget.mini.zoom125", TileDensity.Mini, 125), HasMinSize: true),
            new("widget.full.zoom150", GroupWidget, context => ZoomedWidget(context, "widget.full.zoom150", TileDensity.Full, 150), HasMinSize: true),
            new("widget.mini.zoom150", GroupWidget, context => ZoomedWidget(context, "widget.mini.zoom150", TileDensity.Mini, 150), HasMinSize: true),
            new("widget.daytile.full", GroupWidget, context => DayTile(context, "widget.daytile.full", TileDensity.Full, wide: false), HasMinSize: true),
            new("widget.daytile.mini", GroupWidget, context => DayTile(context, "widget.daytile.mini", TileDensity.Mini, wide: false), HasMinSize: true),
            new("widget.daytile.full.wide", GroupWidget, context => DayTile(context, "widget.daytile.full.wide", TileDensity.Full, wide: true)),
            new("widget.daytile.mini.wide", GroupWidget, context => DayTile(context, "widget.daytile.mini.wide", TileDensity.Mini, wide: true)),
            new("stats.window", GroupStats, context => StatsWindowWithLimits(context), HasMinSize: true),
            new("stats.agents", GroupStats, context => Program.RenderStatistics(
                context.NextDataFolder("stats"), WithSubagents(context.Records), context.BusyDay, fullHeight: true,
                context.OutputPath("stats.agents"), minWidth: context.Size == SizeKind.Min,
                configure: viewModel => viewModel.SetColorByCommand.Execute(StatsColorBy.Agent)), HasMinSize: true),
            new("stats.sessions", GroupStats, context => Program.RenderStatistics(
                context.NextDataFolder("stats"), context.Records, context.BusyDay, fullHeight: true,
                context.OutputPath("stats.sessions"), minWidth: context.Size == SizeKind.Min,
                seed: store => SeedSessions(store, context.Records),
                configure: viewModel => viewModel.SetGroupingCommand.Execute(StatsGrouping.Session)), HasMinSize: true),
            new("stats.filtered", GroupStats, context => Program.RenderStatistics(
                context.NextDataFolder("stats"), context.Records, context.BusyDay, fullHeight: true,
                context.OutputPath("stats.filtered"), minWidth: context.Size == SizeKind.Min,
                configure: ApplySampleFilters), HasMinSize: true),
        };

        foreach (var category in settingsCategories)
        {
            var id = "settings." + category.ToLowerInvariant();
            list.Add(new Surface(id, GroupSettings, context => ReviewSettings.RenderCategory(context, id, category), HasMinSize: true));
        }
        list.Add(new Surface("settings.system-moving", GroupSettings,
            context => ReviewSettings.RenderMoving(context, "settings.system-moving"), HasMinSize: true));

        list.Add(new Surface("tile.failed", GroupTiles, context => ReviewDialogs.RenderFailedTile(context, "tile.failed"), HasMinSize: true));
        list.Add(new Surface("tile.blocked", GroupTiles, context => ReviewDialogs.RenderBlockedTile(context, "tile.blocked"), HasMinSize: true));

        list.Add(new Surface("dialog.signin-blocked", GroupDialogs, context => ReviewDialogs.RenderBlockedSignIn(context.OutputPath("dialog.signin-blocked"))));
        list.Add(new Surface("dialog.welcome", GroupDialogs, context => ReviewDialogs.RenderWelcome(context.Now, context.OutputPath("dialog.welcome"))));
        list.Add(new Surface("dialog.remove-account", GroupDialogs, context => ReviewDialogs.RenderRemoveAccountConfirm(context.OutputPath("dialog.remove-account"))));
        list.Add(new Surface("dialog.crash", GroupDialogs, context => ReviewDialogs.RenderCrash(context.OutputPath("dialog.crash"))));

        list.Add(new Surface("menu.titlebar", GroupMenus, context => ReviewMenus.RenderTitleBarMenu(context, "menu.titlebar")));
        list.Add(new Surface("popup.layout", GroupMenus, context => ReviewMenus.RenderLayoutPopup(context, "popup.layout")));
        list.Add(new Surface("menu.tray", GroupMenus, context => ReviewMenus.RenderTrayMenu(context, "menu.tray")));
        list.Add(new Surface("menu.tile", GroupMenus, context => ReviewMenus.RenderTileMenu(context, "menu.tile")));
        list.Add(new Surface("menu.tile-failed", GroupMenus, context => ReviewMenus.RenderFailedTileMenu(context, "menu.tile-failed")));
        list.Add(new Surface("menu.daytile", GroupMenus, context => ReviewMenus.RenderDayTileMenu(context, "menu.daytile")));
        list.Add(new Surface("menu.stats-section", GroupMenus, context => ReviewMenus.RenderStatsSectionMenu(context, "menu.stats-section")));
        return list;
    }

    /// <summary>Every fourth Claude record counted as subagent work, so the share card has something to show.</summary>
    /// <summary>The statistics window with quota history, so the limits section shows rows.</summary>
    private static void StatsWindowWithLimits(SurfaceContext context)
    {
        var folder = context.NextDataFolder("stats");
        Program.RenderStatistics(
            folder, context.Records, context.BusyDay, fullHeight: true,
            context.OutputPath("stats.window"), minWidth: context.Size == SizeKind.Min,
            seed: _ => SampleLimits.Seed(folder, context.Now));
    }

    private static IReadOnlyList<StatsRecord> WithSubagents(IReadOnlyList<StatsRecord> records) =>
        records.Select((record, index) => record.Provider == "claude" && index % 4 == 0 ? record with { Subagent = true } : record).ToList();

    /// <summary>The newest sample days as a few dozen invented sessions, so the session table and bars fill.</summary>
    private static void SeedSessions(StatsStore store, IReadOnlyList<StatsRecord> records)
    {
        var projects = records.Select(record => record.Project).Where(project => project.Length > 0).Distinct().ToList();
        var random = new Random(7);
        var deltas = new List<StatsSessionDelta>();
        for (var i = 0; i < 40; i++)
        {
            var start = DateTimeOffset.Now.AddDays(-random.Next(0, 28)).AddHours(-random.Next(0, 10));
            var tokens = (long)(2_000_000 * Math.Exp(0.9 * (random.NextDouble() * 2 - 1)) * 5);
            var model = i % 3 == 0 ? "claude-sonnet-4-5" : "claude-opus-4-5";
            deltas.Add(new StatsSessionDelta(
                "claude", "sample-" + i, projects[random.Next(projects.Count)], start, start.AddMinutes(10 + random.Next(0, 240)),
                tokens, tokens / 8, tokens / 5, tokens * 3, 0,
                new Dictionary<string, long> { [model] = tokens + tokens / 8 + tokens / 5 + tokens * 3 }));
        }

        store.ApplyIndexResult([], new StatsSourceFileState("sample-sessions", "claude", 0, 0, DateTime.UtcNow), deltas);
    }

    /// <summary>One provider, the largest model and the largest project picked, as after a few clicks.</summary>
    private static void ApplySampleFilters(StatsViewModel viewModel)
    {
        viewModel.SetProviderCommand.Execute("claude");
        viewModel.ToggleFilter(StatsFilterKind.Model, viewModel.ModelShareSlices[0].Label);
        if (viewModel.TopProjectRows.Count > 0)
            viewModel.ToggleFilter(StatsFilterKind.Project, viewModel.TopProjectRows[0].FullPath);
    }

    /// <summary>The widget at a zoom step: the window is as wide as the content needs at that zoom (the
    /// 100 % width times the factor), or at its narrowest for the min size.</summary>
    private static void ZoomedWidget(SurfaceContext context, string id, TileDensity density, int percent)
    {
        var zoom = percent / 100.0;
        Program.RenderWidget(
            context.DataDirectory, context.Samples, context.Now, context.AllProviders, density,
            context.OutputPath(id), context.Pick(WidgetWidth * zoom, WindowPlacementService.MinWidthFor(zoom)), zoom);
    }

    private static void DayTile(SurfaceContext context, string id, TileDensity density, bool wide) =>
        Program.RenderDayTile(
            context.NextDataFolder("daytile"), context.Records,
            wide ? WideTileWidth : context.Pick(WidgetWidth, MinWidgetWidth), density, context.OutputPath(id));
}

/// <summary>The arguments after <c>--review &lt;dir&gt;</c>, checked against the surface list.</summary>
internal sealed class ReviewOptions
{
    private const string Usage =
        "Options after --review <folder>: --surfaces <ids and/or @groups, comma separated> (default all), "
        + "--lang en|de|all (default all), --size min|default|both (default both), --theme <name>|all (default all).";

    internal IReadOnlyList<string> SurfaceTokens { get; private set; } = [];

    internal IReadOnlyList<string> Languages { get; private set; } = ["en", "de"];

    internal IReadOnlyList<SizeKind> Sizes { get; private set; } = [SizeKind.Min, SizeKind.Default];

    internal string Theme { get; private set; } = "all";

    /// <summary>Reads the options that follow the review folder. An unknown option or a missing value
    /// is an error message; a value that needs the surface list is checked later in
    /// <see cref="Resolve"/>.</summary>
    internal static ReviewOptions? Parse(string[] args, int firstOption, out string? error)
    {
        var options = new ReviewOptions();
        error = null;
        for (var i = firstOption; i < args.Length; i++)
        {
            var name = args[i];
            if (name is not ("--surfaces" or "--lang" or "--size" or "--theme"))
            {
                error = $"Unknown argument '{name}'. {Usage}";
                return null;
            }
            if (i + 1 >= args.Length)
            {
                error = $"{name} needs a value. {Usage}";
                return null;
            }
            var value = args[++i];
            switch (name)
            {
                case "--surfaces":
                    options.SurfaceTokens = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--lang":
                    if (value is "en" or "de") options.Languages = [value];
                    else if (value == "all") options.Languages = ["en", "de"];
                    else { error = $"Unknown --lang value '{value}'. Valid: en, de, all."; return null; }
                    break;
                case "--size":
                    if (value == "min") options.Sizes = [SizeKind.Min];
                    else if (value == "default") options.Sizes = [SizeKind.Default];
                    else if (value == "both") options.Sizes = [SizeKind.Min, SizeKind.Default];
                    else { error = $"Unknown --size value '{value}'. Valid: min, default, both."; return null; }
                    break;
                default:
                    options.Theme = value;
                    break;
            }
        }
        return options;
    }

    /// <summary>The themes to draw; null plus an error when the name is unknown.</summary>
    internal IReadOnlyList<(string Name, AppTheme Theme, bool HighContrast)>? ResolveThemes(out string? error)
    {
        error = null;
        if (Theme.Equals("all", StringComparison.OrdinalIgnoreCase))
            return ReviewSurfaces.Themes;
        var match = ReviewSurfaces.Themes.Where(t => t.Name.Equals(Theme, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count > 0)
            return match;
        error = $"Unknown --theme value '{Theme}'. Valid: {string.Join(", ", ReviewSurfaces.Themes.Select(t => t.Name))}, all.";
        return null;
    }

    /// <summary>The surfaces to draw, in registry order; null plus an error naming the bad token and
    /// listing every valid id and group.</summary>
    internal IReadOnlyList<Surface>? Resolve(IReadOnlyList<Surface> all, out string? error)
    {
        error = null;
        if (SurfaceTokens.Count == 0)
            return all;

        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in SurfaceTokens)
        {
            var matches = token.StartsWith('@')
                ? all.Where(s => s.Group.Equals(token, StringComparison.OrdinalIgnoreCase)).ToList()
                : all.Where(s => s.Id.Equals(token, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                error = $"Unknown surface '{token}'. Valid groups: {string.Join(", ", ReviewSurfaces.Groups)}. "
                    + $"Valid ids: {string.Join(", ", all.Select(s => s.Id))}.";
                return null;
            }
            foreach (var match in matches)
                chosen.Add(match.Id);
        }
        return all.Where(s => chosen.Contains(s.Id)).ToList();
    }
}
