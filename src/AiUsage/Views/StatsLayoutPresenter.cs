using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AiUsage.Stats;
using AiUsage.Views.Controls;

namespace AiUsage.Views;

/// <summary>
/// Builds the statistics window's section area from a <see cref="StatsLayoutRow"/> list: a single
/// row puts its section straight into the host panel, a two-column row becomes a grid with two equal
/// columns, each a grid of stacked sections whose rows follow the sections' expansion. Every
/// section is moved, never copied, so its bindings and state stay intact.
/// </summary>
internal sealed class StatsLayoutPresenter
{
    private const double RowGap = 12;
    private const double ColumnGap = 6;
    private const double MoveMilliseconds = 260;

    private readonly Panel _host;
    private readonly IReadOnlyDictionary<string, CollapsibleSection> _sections;
    private readonly List<RowDefinition> _boundRows = [];
    private readonly List<HostEntry> _entries = [];
    private readonly EventHandler _visibilityChanged;
    private bool _applying;

    public StatsLayoutPresenter(Panel host, IReadOnlyDictionary<string, CollapsibleSection> sections)
    {
        _host = host;
        _sections = sections;
        _visibilityChanged = (_, _) => RefreshVisibility();
        var descriptor = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(CollapsibleSection));
        foreach (var section in sections.Values)
            descriptor.AddValueChanged(section, _visibilityChanged);
    }

    /// <summary>The panel the sections are laid out in.</summary>
    public Panel Host => _host;

    /// <summary>Every section by its key.</summary>
    public IReadOnlyDictionary<string, CollapsibleSection> Sections => _sections;

    /// <summary>The arrangement applied last, normalized.</summary>
    public IReadOnlyList<StatsLayoutRow> Current { get; private set; } = StatsLayout.Default();

    /// <summary>Rebuilds the host from <paramref name="rows"/> and lets every section that changed
    /// place slide from where it was shown to its new spot. <paramref name="flown"/>, when given,
    /// starts from <paramref name="flownFrom"/> (host coordinates) instead: the spot where the
    /// dragged picture of it was let go. Without client area animations this is a plain Apply.</summary>
    public void ApplyAnimated(IReadOnlyList<StatsLayoutRow> rows, CollapsibleSection? flown = null, Point flownFrom = default)
    {
        if (!SystemParameters.ClientAreaAnimation || !_host.IsLoaded)
        {
            Apply(rows);
            return;
        }

        // Captured with any running slide included, so a second move starts from what is on screen.
        var before = new Dictionary<CollapsibleSection, Point>();
        foreach (var section in _sections.Values)
        {
            if (section.IsVisible)
                before[section] = section.TranslatePoint(new Point(0, 0), _host);
        }

        if (flown is not null && before.ContainsKey(flown))
            before[flown] = flownFrom;

        Apply(rows);
        foreach (var section in before.Keys)
            section.RenderTransform = Transform.Identity;
        _host.UpdateLayout();

        var duration = TimeSpan.FromMilliseconds(MoveMilliseconds);
        foreach (var (section, old) in before)
        {
            if (!section.IsVisible)
                continue;

            var now = section.TranslatePoint(new Point(0, 0), _host);
            var dx = old.X - now.X;
            var dy = old.Y - now.Y;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
                continue;

            var shift = new TranslateTransform(dx, dy);
            section.RenderTransform = shift;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, duration) { EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, duration) { EasingFunction = ease });
        }
    }

    /// <summary>Rebuilds the host from <paramref name="rows"/>.</summary>
    public void Apply(IReadOnlyList<StatsLayoutRow> rows)
    {
        Current = StatsLayout.Normalize(rows);
        _applying = true;
        try
        {
            foreach (var row in _boundRows)
                BindingOperations.ClearBinding(row, RowDefinition.HeightProperty);
            _boundRows.Clear();
            _entries.Clear();

            // Unhook every section from wherever it sits now before the containers are dropped.
            foreach (var section in _sections.Values)
            {
                if (LogicalTreeHelper.GetParent(section) is Panel parent)
                    parent.Children.Remove(section);
            }

            _host.Children.Clear();

            foreach (var row in Current)
            {
                if (row.Right.Count == 0)
                {
                    var section = _sections[row.Left[0]];
                    _entries.Add(new HostEntry(section, null, null, null));
                    _host.Children.Add(section);
                    continue;
                }

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
                var left = BuildColumn(grid, 0, row.Left);
                var right = BuildColumn(grid, 1, row.Right);
                _entries.Add(new HostEntry(null, grid, left, right));
                _host.Children.Add(grid);
            }
        }
        finally
        {
            _applying = false;
        }

        RefreshVisibility();
    }

    /// <summary>Stops listening to the sections' visibility; call when the window closes.</summary>
    public void Detach()
    {
        var descriptor = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(CollapsibleSection));
        foreach (var section in _sections.Values)
            descriptor.RemoveValueChanged(section, _visibilityChanged);
    }

    private Column BuildColumn(Grid outer, int columnIndex, List<string> keys)
    {
        var inner = new Grid();
        Grid.SetColumn(inner, columnIndex);
        var members = new List<ColumnMember>();
        foreach (var key in keys)
        {
            var section = _sections[key];
            var definition = new RowDefinition();
            BindExpansion(definition, section);
            _boundRows.Add(definition);
            inner.RowDefinitions.Add(definition);
            Grid.SetRow(section, members.Count);
            inner.Children.Add(section);
            members.Add(new ColumnMember(section, definition));
        }

        outer.Children.Add(inner);
        return new Column(inner, members);
    }

    private static void BindExpansion(RowDefinition definition, CollapsibleSection section) =>
        BindingOperations.SetBinding(definition, RowDefinition.HeightProperty,
            new Binding(nameof(CollapsibleSection.IsExpanded)) { Source = section, Converter = new ExpandedToRowHeightConverter() });

    /// <summary>Re-sets column widths, gaps and container visibility only; nothing is re-parented
    /// here, since moving a section changes its inherited data context and would flip the table's
    /// visibility trigger again.</summary>
    private void RefreshVisibility()
    {
        if (_applying)
            return;

        var lastVisible = _entries.FindLastIndex(IsEntryVisible);
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var gap = i == lastVisible ? 0 : RowGap;
            if (entry.Section is { } single)
            {
                single.Margin = new Thickness(0, 0, 0, gap);
                continue;
            }

            var grid = entry.Grid!;
            var leftShown = ColumnShown(entry.Left!);
            var rightShown = ColumnShown(entry.Right!);
            grid.Visibility = leftShown || rightShown ? Visibility.Visible : Visibility.Collapsed;
            grid.Margin = new Thickness(0, 0, 0, gap);
            grid.ColumnDefinitions[0].Width = leftShown ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            grid.ColumnDefinitions[1].Width = rightShown ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            var both = leftShown && rightShown;
            entry.Left!.Grid.Margin = new Thickness(0, 0, both ? ColumnGap : 0, 0);
            entry.Right!.Grid.Margin = new Thickness(both ? ColumnGap : 0, 0, 0, 0);
            ArrangeColumn(entry.Left!);
            ArrangeColumn(entry.Right!);
        }
    }

    private static bool IsEntryVisible(HostEntry entry) =>
        entry.Section is { } single
            ? single.Visibility == Visibility.Visible
            : ColumnShown(entry.Left!) || ColumnShown(entry.Right!);

    private static bool ColumnShown(Column column) =>
        column.Members.Any(m => m.Section.Visibility == Visibility.Visible);

    /// <summary>A stacked section hides together with its grid row, and the last visible one in
    /// the column carries no bottom gap.</summary>
    private static void ArrangeColumn(Column column)
    {
        var last = column.Members.FindLastIndex(m => m.Section.Visibility == Visibility.Visible);
        for (var i = 0; i < column.Members.Count; i++)
        {
            var member = column.Members[i];
            var shown = member.Section.Visibility == Visibility.Visible;
            member.Section.Margin = new Thickness(0, 0, 0, shown && i != last ? RowGap : 0);
            if (shown)
            {
                if (!BindingOperations.IsDataBound(member.Row, RowDefinition.HeightProperty))
                    BindExpansion(member.Row, member.Section);
            }
            else
            {
                BindingOperations.ClearBinding(member.Row, RowDefinition.HeightProperty);
                member.Row.Height = new GridLength(0);
            }
        }
    }

    private sealed record ColumnMember(CollapsibleSection Section, RowDefinition Row);

    private sealed record Column(Grid Grid, List<ColumnMember> Members);

    private sealed record HostEntry(CollapsibleSection? Section, Grid? Grid, Column? Left, Column? Right);
}
