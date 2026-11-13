using System.Threading;
using System.Windows;
using System.Windows.Controls;
using AiUsage.Stats;
using AiUsage.Views;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Drives <see cref="StatsLayoutPresenter"/> with bare (never shown) sections in a plain panel on an
/// STA thread: how the host is rebuilt from a row list, how gaps and column widths follow each
/// section's visibility, and that sections are moved rather than copied.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class StatsLayoutPresenterTests
{
    private const double RowGap = 12;
    private const double ColumnGap = 6;

    [Fact]
    public void The_default_arrangement_puts_three_sections_straight_into_the_host_and_one_two_column_grid()
    {
        Sta(() =>
        {
            var (host, presenter) = Create();

            presenter.Apply(StatsLayout.Default());

            Assert.Equal(4, host.Children.Count);
            Assert.Same(presenter.Sections["monthgrid"], host.Children[0]);
            Assert.Same(presenter.Sections["figures"], host.Children[1]);
            Assert.Same(presenter.Sections["breakdown"], host.Children[2]);
            var grid = Assert.IsType<Grid>(host.Children[3]);
            Assert.Equal(2, grid.ColumnDefinitions.Count);
            Assert.Equal(2, grid.Children.Count);
        });
    }

    [Fact]
    public void Each_column_of_a_two_column_row_stacks_its_sections_in_order()
    {
        Sta(() =>
        {
            var (host, presenter) = Create();

            presenter.Apply(StatsLayout.Default());

            var grid = (Grid)host.Children[3];
            var left = (Grid)grid.Children[0];
            var right = (Grid)grid.Children[1];
            Assert.Equal(0, Grid.GetColumn(left));
            Assert.Equal(1, Grid.GetColumn(right));
            Assert.Equal([presenter.Sections["provider"], presenter.Sections["model"], presenter.Sections["effort"]], left.Children.Cast<UIElement>());
            Assert.Equal([presenter.Sections["planforecast"], presenter.Sections["limits"], presenter.Sections["projects"]], right.Children.Cast<UIElement>());
            Assert.Equal(3, left.RowDefinitions.Count);
            Assert.Equal(2, Grid.GetRow(presenter.Sections["effort"]));
        });
    }

    [Fact]
    public void An_empty_row_list_is_repaired_to_one_single_row_per_section()
    {
        Sta(() =>
        {
            var (host, presenter) = Create();

            presenter.Apply([]);

            Assert.Equal(StatsLayout.SectionKeys.Count, host.Children.Count);
            Assert.Equal(StatsLayout.SectionKeys.Count, presenter.Current.Count);
            Assert.All(host.Children.Cast<UIElement>(), child => Assert.IsType<CollapsibleSection>(child));
        });
    }

    [Fact]
    public void Unknown_and_repeated_keys_are_dropped_and_Current_holds_the_normalized_rows()
    {
        Sta(() =>
        {
            var (host, presenter) = Create();
            var rows = new List<StatsLayoutRow>
            {
                new() { Left = ["figures", "nonsense"], Right = [] },
                new() { Left = ["figures"], Right = [] },
            };

            presenter.Apply(rows);

            Assert.Equal(StatsLayout.SectionKeys.Count, host.Children.Count);
            Assert.Equal(["figures"], presenter.Current[0].Left);
            Assert.Equal(StatsLayout.SectionKeys.Count, presenter.Current.SelectMany(r => r.Left.Concat(r.Right)).Distinct().Count());
        });
    }

    [Fact]
    public void Applying_again_moves_the_sections_to_their_new_parent_and_never_leaves_one_in_two_places()
    {
        Sta(() =>
        {
            var (host, presenter) = Create();
            presenter.Apply(StatsLayout.Default());
            var provider = presenter.Sections["provider"];
            Assert.IsType<Grid>(LogicalTreeHelper.GetParent(provider));

            presenter.Apply([]);

            Assert.Same(host, LogicalTreeHelper.GetParent(provider));
            Assert.Equal(StatsLayout.SectionKeys.Count, host.Children.Count);
            Assert.Equal(1, host.Children.Cast<UIElement>().Count(c => ReferenceEquals(c, provider)));
        });
    }

    [Fact]
    public void Only_the_last_visible_single_row_loses_its_bottom_gap()
    {
        Sta(() =>
        {
            var (_, presenter) = Create();
            presenter.Apply([]);
            var keys = StatsLayout.SectionKeys;

            foreach (var key in keys.Take(keys.Count - 1))
                Assert.Equal(RowGap, presenter.Sections[key].Margin.Bottom);
            Assert.Equal(0, presenter.Sections[keys[^1]].Margin.Bottom);
        });
    }

    [Fact]
    public void Hiding_the_last_section_hands_the_missing_gap_to_the_one_before()
    {
        Sta(() =>
        {
            var (_, presenter) = Create();
            presenter.Apply([]);
            var keys = StatsLayout.SectionKeys;

            presenter.Sections[keys[^1]].Visibility = Visibility.Collapsed;

            Assert.Equal(0, presenter.Sections[keys[^2]].Margin.Bottom);
            Assert.Equal(RowGap, presenter.Sections[keys[^3]].Margin.Bottom);
        });
    }

    [Fact]
    public void Two_visible_columns_are_equal_stars_with_a_gap_between_them()
    {
        Sta(() =>
        {
            var (host, presenter) = Create();

            presenter.Apply(StatsLayout.Default());

            var grid = (Grid)host.Children[3];
            Assert.Equal(new GridLength(1, GridUnitType.Star), grid.ColumnDefinitions[0].Width);
            Assert.Equal(new GridLength(1, GridUnitType.Star), grid.ColumnDefinitions[1].Width);
            Assert.Equal(new Thickness(0, 0, ColumnGap, 0), ((Grid)grid.Children[0]).Margin);
            Assert.Equal(new Thickness(ColumnGap, 0, 0, 0), ((Grid)grid.Children[1]).Margin);
            Assert.Equal(Visibility.Visible, grid.Visibility);
        });
    }

    [Fact]
    public void A_column_whose_sections_are_all_hidden_gives_its_width_to_the_other_column()
    {
        Sta(() =>
        {
            var (host, presenter) = Create();
            presenter.Apply(StatsLayout.Default());
            var grid = (Grid)host.Children[3];

            foreach (var key in new[] { "planforecast", "limits", "projects" })
                presenter.Sections[key].Visibility = Visibility.Collapsed;

            Assert.Equal(new GridLength(1, GridUnitType.Star), grid.ColumnDefinitions[0].Width);
            Assert.Equal(new GridLength(0), grid.ColumnDefinitions[1].Width);
            Assert.Equal(new Thickness(0), ((Grid)grid.Children[0]).Margin);
            Assert.Equal(Visibility.Visible, grid.Visibility);
        });
    }

    [Fact]
    public void A_two_column_row_with_every_section_hidden_collapses_and_the_row_before_it_is_last()
    {
        Sta(() =>
        {
            var (host, presenter) = Create();
            presenter.Apply(StatsLayout.Default());
            var grid = (Grid)host.Children[3];

            foreach (var key in new[] { "provider", "model", "effort", "planforecast", "limits", "projects" })
                presenter.Sections[key].Visibility = Visibility.Collapsed;

            Assert.Equal(Visibility.Collapsed, grid.Visibility);
            Assert.Equal(0, presenter.Sections["breakdown"].Margin.Bottom);
            Assert.Equal(RowGap, presenter.Sections["figures"].Margin.Bottom);
        });
    }

    [Fact]
    public void A_hidden_stacked_section_gets_a_zero_row_and_the_last_visible_one_no_bottom_gap()
    {
        Sta(() =>
        {
            var (host, presenter) = Create();
            presenter.Apply(StatsLayout.Default());
            var left = (Grid)((Grid)host.Children[3]).Children[0];

            presenter.Sections["effort"].Visibility = Visibility.Collapsed;

            Assert.Equal(new GridLength(0), left.RowDefinitions[2].Height);
            Assert.Equal(0, presenter.Sections["effort"].Margin.Bottom);
            Assert.Equal(0, presenter.Sections["model"].Margin.Bottom);
            Assert.Equal(RowGap, presenter.Sections["provider"].Margin.Bottom);
        });
    }

    [Fact]
    public void A_stacked_row_follows_its_section_expansion_and_comes_back_when_the_section_is_shown_again()
    {
        Sta(() =>
        {
            var (host, presenter) = Create();
            presenter.Apply(StatsLayout.Default());
            var left = (Grid)((Grid)host.Children[3]).Children[0];
            var model = presenter.Sections["model"];

            Assert.Equal(new GridLength(1, GridUnitType.Star), left.RowDefinitions[1].Height);
            model.IsExpanded = false;
            Assert.True(left.RowDefinitions[1].Height.IsAuto);

            model.Visibility = Visibility.Collapsed;
            Assert.Equal(new GridLength(0), left.RowDefinitions[1].Height);
            model.Visibility = Visibility.Visible;
            Assert.True(left.RowDefinitions[1].Height.IsAuto);
            model.IsExpanded = true;
            Assert.Equal(new GridLength(1, GridUnitType.Star), left.RowDefinitions[1].Height);
        });
    }

    [Fact]
    public void A_section_that_sizes_to_its_content_keeps_an_auto_row_while_expanded()
    {
        Sta(() =>
        {
            var (host, presenter) = Create();
            presenter.Sections["model"].SizesToContent = true;

            presenter.Apply(StatsLayout.Default());

            var left = (Grid)((Grid)host.Children[3]).Children[0];
            Assert.True(left.RowDefinitions[1].Height.IsAuto);
            Assert.Equal(new GridLength(1, GridUnitType.Star), left.RowDefinitions[0].Height);
        });
    }

    [Fact]
    public void Detach_stops_the_presenter_from_reacting_to_visibility_changes()
    {
        Sta(() =>
        {
            var (_, presenter) = Create();
            presenter.Apply([]);
            var keys = StatsLayout.SectionKeys;

            presenter.Detach();
            presenter.Sections[keys[^1]].Visibility = Visibility.Collapsed;

            Assert.Equal(0, presenter.Sections[keys[^1]].Margin.Bottom);
            Assert.Equal(RowGap, presenter.Sections[keys[^2]].Margin.Bottom);
        });
    }

    [Fact]
    public void An_animated_apply_on_a_host_that_is_not_loaded_rebuilds_without_animation()
    {
        Sta(() =>
        {
            var (host, presenter) = Create();
            var moved = presenter.Sections["limits"];

            presenter.ApplyAnimated(StatsLayout.Default(), moved, new Point(10, 20));

            Assert.Equal(4, host.Children.Count);
            Assert.Equal(StatsLayout.Default().Count, presenter.Current.Count);
            Assert.True(moved.RenderTransform is null || moved.RenderTransform == System.Windows.Media.Transform.Identity);
        });
    }

    [Fact]
    public void The_host_and_sections_are_exposed_as_given()
    {
        Sta(() =>
        {
            var (host, presenter) = Create();

            Assert.Same(host, presenter.Host);
            Assert.Equal(StatsLayout.SectionKeys.OrderBy(k => k), presenter.Sections.Keys.OrderBy(k => k));
            Assert.Equal(StatsLayout.Default().Count, presenter.Current.Count);
        });
    }

    private static (StackPanel Host, StatsLayoutPresenter Presenter) Create()
    {
        var sections = StatsLayout.SectionKeys.ToDictionary(key => key, key => new CollapsibleSection { Title = key });
        var host = new StackPanel();
        return (host, new StatsLayoutPresenter(host, sections));
    }

    private static void Sta(Action body)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Layout presenter test did not finish");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
