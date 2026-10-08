using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Views.Controls;

namespace AiUsage.Views;

/// <summary>Maps the pointer position over a drop target to the side the dragged section lands on.</summary>
internal static class StatsDropZone
{
    /// <summary>Null outside the target. Beside a section that is alone in its row the outer quarters
    /// mean Left and Right; everywhere else the upper half means Above and the lower half Below.</summary>
    public static StatsDropPosition? Compute(double width, double height, double x, double y, bool targetAlone)
    {
        if (!Contains(width, height, x, y))
            return null;

        if (targetAlone)
        {
            if (x < 0.25 * width)
                return StatsDropPosition.Left;
            if (x > 0.75 * width)
                return StatsDropPosition.Right;
        }

        return y < height / 2 ? StatsDropPosition.Above : StatsDropPosition.Below;
    }

    /// <summary>True when the point lies inside a rectangle of this size anchored at the origin.</summary>
    public static bool Contains(double width, double height, double x, double y) =>
        x >= 0 && x <= width && y >= 0 && y <= height;
}

/// <summary>
/// Lets the user pick a statistics section up by its heading and drop it somewhere else. A press on
/// the heading button stays a plain click until the pointer has moved a system drag distance; from
/// then on the section owns the mouse, a picture of it follows the pointer, a colored line glides to
/// where it would land, and releasing the button hands the new arrangement plus the picture's spot
/// to the commit callback, so the section can slide from there into place. Escape or a lost capture
/// cancels.
/// </summary>
internal sealed class StatsSectionDragController
{
    private const double ScrollZone = 48;
    private const double MarkerGap = 6;
    private const double MarkerThickness = 3;
    private const double DraggedOpacity = 0.4;
    // Auto-scroll speed in pixels per second: a slow base at the zone's edge, rising with the
    // square of how deep the pointer sits in it (capped at 1.5 zones past the edge).
    private const double ScrollBaseSpeed = 150;
    private const double ScrollDepthSpeed = 700;

    private readonly Window _window;
    private readonly ScrollViewer _scroller;
    private readonly StatsLayoutPresenter _presenter;
    private readonly Action<IReadOnlyList<StatsLayoutRow>, CollapsibleSection?, Point> _commit;

    private CollapsibleSection? _pressed;
    private Point _pressPoint;
    private Point _grabOffset;
    private CollapsibleSection? _dragged;
    private CollapsibleSection? _target;
    private StatsDropPosition _position;
    private StatsDropMarkerAdorner? _marker;
    private StatsDragGhostAdorner? _ghost;
    private AdornerLayer? _markerLayer;
    private bool _framing;
    private TimeSpan _lastFrame;

    public StatsSectionDragController(Window window, ScrollViewer scroller, StatsLayoutPresenter presenter, Action<IReadOnlyList<StatsLayoutRow>, CollapsibleSection?, Point> commit)
    {
        _window = window;
        _scroller = scroller;
        _presenter = presenter;
        _commit = commit;

        foreach (var section in presenter.Sections.Values)
        {
            section.ApplyTemplate();
            section.PreviewMouseLeftButtonDown += Section_PreviewMouseLeftButtonDown;
            section.PreviewMouseMove += Section_PreviewMouseMove;
            section.PreviewMouseLeftButtonUp += Section_PreviewMouseLeftButtonUp;
            section.LostMouseCapture += Section_LostMouseCapture;
        }
    }

    public bool IsDragging => _dragged is not null;

    /// <summary>Ends a running drag without changing the layout.</summary>
    public void Cancel() => End();

    private void Section_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var section = (CollapsibleSection)sender;
        _pressed = null;
        if (IsDragging || !IsInsideHeaderButton(section, e.OriginalSource as DependencyObject))
            return;

        _pressed = section;
        _pressPoint = e.GetPosition(_window);
        _grabOffset = e.GetPosition(section);
    }

    private void Section_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var section = (CollapsibleSection)sender;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (_pressed == section)
                _pressed = null;
            return;
        }

        if (_dragged is null)
        {
            if (_pressed != section)
                return;

            var now = e.GetPosition(_window);
            if (Math.Abs(now.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(now.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            Begin(section);
            return;
        }

        if (_dragged == section)
        {
            UpdateTarget();
            UpdateGhost();
        }
    }

    private void Section_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var section = (CollapsibleSection)sender;
        if (_dragged != section)
        {
            if (_pressed == section)
                _pressed = null;
            return;
        }

        var key = section.SectionKey;
        var target = _target;
        var position = _position;
        var dropPoint = GhostOrigin();
        End();
        if (target is not null)
            _commit(StatsLayout.Move(_presenter.Current, key, target.SectionKey, position), section, dropPoint);
    }

    private void Section_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragged is not null && ReferenceEquals(e.OriginalSource, _dragged))
            Cancel();
    }

    private void Begin(CollapsibleSection section)
    {
        _pressed = null;
        // The heading button loses its capture here and reports it synchronously, so the flag is
        // only set once CaptureMouse has returned.
        if (!section.CaptureMouse())
            return;

        // The picture is taken before the section dims, so it shows the section at full strength.
        var snapshot = StatsDragGhostAdorner.Snapshot(section);
        _dragged = section;
        section.Opacity = DraggedOpacity;
        CursorRestore.Override(Cursors.SizeAll);
        _markerLayer = AdornerLayer.GetAdornerLayer(_presenter.Host);
        if (_markerLayer is not null)
        {
            var accent = (Brush)_window.FindResource("Accent");
            _marker = new StatsDropMarkerAdorner(_presenter.Host, accent);
            _markerLayer.Add(_marker);
            if (snapshot is { } picture)
            {
                _ghost = new StatsDragGhostAdorner(_presenter.Host, picture.Image, picture.Size, picture.Clipped, accent);
                _markerLayer.Add(_ghost);
            }
        }

        StartFrames();
        UpdateTarget();
        UpdateGhost();
    }

    private void End()
    {
        var section = _dragged;
        _dragged = null;
        _pressed = null;
        _target = null;
        StopFrames();
        CursorRestore.Reset();
        if (_marker is not null)
            _markerLayer?.Remove(_marker);
        if (_ghost is not null)
            _markerLayer?.Remove(_ghost);
        _marker = null;
        _ghost = null;
        _markerLayer = null;
        if (section is not null)
        {
            section.Opacity = 1;
            if (SystemParameters.ClientAreaAnimation)
            {
                section.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(DraggedOpacity, 1, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop });
            }

            section.ReleaseMouseCapture();
        }
    }

    private void UpdateTarget()
    {
        var host = _presenter.Host;
        var point = Mouse.GetPosition(host);
        var inScroller = Mouse.GetPosition(_scroller);
        if (!StatsDropZone.Contains(host.ActualWidth, host.ActualHeight, point.X, point.Y)
            || !StatsDropZone.Contains(_scroller.ActualWidth, _scroller.ActualHeight, inScroller.X, inScroller.Y))
        {
            // Far outside the sections: releasing here is a plain cancel, not the last marked spot.
            _target = null;
            _marker?.Update(null);
            return;
        }

        // A gap between sections keeps the previous target so the marker does not flicker.
        var hit = VisualTreeHelper.HitTest(host, point);
        if (hit is null)
            return;

        var section = FindSection(hit.VisualHit);
        if (section is null)
            return;

        if (section == _dragged)
        {
            _target = null;
            _marker?.Update(null);
            return;
        }

        var local = Mouse.GetPosition(section);
        var alone = _presenter.Current.Any(row => row.Right.Count == 0 && row.Left[0] == section.SectionKey);
        var position = StatsDropZone.Compute(section.ActualWidth, section.ActualHeight, local.X, local.Y, alone);
        if (position is null)
            return;

        _target = section;
        _position = position.Value;
        var bounds = section.TransformToAncestor(host).TransformBounds(new Rect(0, 0, section.ActualWidth, section.ActualHeight));
        _marker?.Update(MarkerRect(bounds, _position));
    }

    private static Rect MarkerRect(Rect bounds, StatsDropPosition position) => position switch
    {
        StatsDropPosition.Above => new Rect(bounds.Left, bounds.Top - MarkerGap - MarkerThickness / 2, bounds.Width, MarkerThickness),
        StatsDropPosition.Below => new Rect(bounds.Left, bounds.Bottom + MarkerGap - MarkerThickness / 2, bounds.Width, MarkerThickness),
        StatsDropPosition.Left => new Rect(bounds.Left - MarkerThickness / 2, bounds.Top, MarkerThickness, bounds.Height),
        _ => new Rect(bounds.Right - MarkerThickness / 2, bounds.Top, MarkerThickness, bounds.Height),
    };

    /// <summary>Where the picture's top left corner sits now, in host coordinates.</summary>
    private Point GhostOrigin()
    {
        var pointer = Mouse.GetPosition(_presenter.Host);
        return new Point(pointer.X - _grabOffset.X, pointer.Y - _grabOffset.Y);
    }

    private void UpdateGhost()
    {
        if (_ghost is null)
            return;

        var viewport = new Rect(_scroller.TranslatePoint(new Point(0, 0), _presenter.Host), new Size(_scroller.ActualWidth, _scroller.ActualHeight));
        _ghost.MoveTo(GhostOrigin(), viewport);
    }

    // Auto-scroll and the marker's glide run once per rendered frame, so both move as smoothly as
    // the screen refreshes instead of in timer steps.
    private void StartFrames()
    {
        if (_framing)
            return;

        _framing = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopFrames()
    {
        if (!_framing)
            return;

        _framing = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_dragged is null || e is not RenderingEventArgs args)
            return;

        // Rendering can fire more than once for the same frame; only a new frame time moves things.
        if (args.RenderingTime == _lastFrame)
            return;

        var seconds = _lastFrame == TimeSpan.Zero ? 0 : Math.Min((args.RenderingTime - _lastFrame).TotalSeconds, 0.05);
        _lastFrame = args.RenderingTime;
        if (seconds <= 0)
            return;

        AutoScroll(seconds);
        _marker?.Step(seconds);
    }

    private CollapsibleSection? FindSection(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is CollapsibleSection section && _presenter.Sections.Values.Contains(section))
                return section;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private void AutoScroll(double seconds)
    {
        var y = Mouse.GetPosition(_scroller).Y;
        double depth = 0;
        if (y < ScrollZone)
            depth = -Math.Min(1.5, (ScrollZone - y) / ScrollZone);
        else if (y > _scroller.ActualHeight - ScrollZone)
            depth = Math.Min(1.5, (y - (_scroller.ActualHeight - ScrollZone)) / ScrollZone);

        if (depth == 0)
            return;

        var speed = ScrollBaseSpeed + ScrollDepthSpeed * depth * depth;
        var before = _scroller.VerticalOffset;
        _scroller.ScrollToVerticalOffset(before + Math.Sign(depth) * speed * seconds);
        _scroller.UpdateLayout();
        if (_scroller.VerticalOffset == before)
            return;

        UpdateTarget();
        UpdateGhost();
    }

    private static bool IsInsideHeaderButton(CollapsibleSection section, DependencyObject? source)
    {
        section.ApplyTemplate();
        if (section.Template.FindName("PART_HeaderButton", section) is not Button button)
            return false;

        for (var node = source; node is not null;
             node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, button))
                return true;
        }

        return false;
    }
}

/// <summary>The colored line that shows where a dragged section would land. It appears in place
/// and then glides to each new spot instead of jumping there.</summary>
internal sealed class StatsDropMarkerAdorner : Adorner
{
    // Exponential approach rate per second: about 95 percent of the way within a tenth of a second.
    private const double GlideRate = 30;
    private const double SnapDistance = 0.3;

    private readonly Brush _brush;
    private Rect? _target;
    private Rect _shown;
    private bool _visible;

    public StatsDropMarkerAdorner(UIElement host, Brush brush)
        : base(host)
    {
        _brush = brush;
        IsHitTestVisible = false;
    }

    /// <summary>The line as drawn right now; null while hidden.</summary>
    internal Rect? ShownLine => _visible ? _shown : null;

    /// <summary>Moves the line toward <paramref name="line"/> (host coordinates); null hides it.</summary>
    public void Update(Rect? line)
    {
        _target = line;
        if (line is not { } rect)
        {
            _visible = false;
        }
        else if (!_visible || !SystemParameters.ClientAreaAnimation)
        {
            _shown = rect;
            _visible = true;
        }

        InvalidateVisual();
    }

    /// <summary>Advances the glide by one frame of <paramref name="seconds"/>.</summary>
    public void Step(double seconds)
    {
        if (!_visible || _target is not { } target || _shown == target)
            return;

        var k = 1 - Math.Exp(-GlideRate * seconds);
        var next = new Rect(
            _shown.X + (target.X - _shown.X) * k,
            _shown.Y + (target.Y - _shown.Y) * k,
            Math.Max(0, _shown.Width + (target.Width - _shown.Width) * k),
            Math.Max(0, _shown.Height + (target.Height - _shown.Height) * k));
        var arrived = Math.Abs(next.X - target.X) < SnapDistance && Math.Abs(next.Y - target.Y) < SnapDistance
            && Math.Abs(next.Width - target.Width) < SnapDistance && Math.Abs(next.Height - target.Height) < SnapDistance;
        _shown = arrived ? target : next;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_visible)
            drawingContext.DrawRoundedRectangle(_brush, null, _shown, 1.5, 1.5);
    }
}

/// <summary>A picture of the dragged section that follows the pointer, cut off with a fade when the
/// section is taller than <see cref="MaxGhostHeight"/>, and kept inside the scroll area.</summary>
internal sealed class StatsDragGhostAdorner : Adorner
{
    public const double MaxGhostHeight = 160;
    private const double Radius = 6;
    private const double FadeHeight = 48;
    private const double GhostOpacity = 0.9;

    private readonly ImageSource _image;
    private readonly Size _size;
    private readonly bool _clipped;
    private readonly Pen _border;
    private Point _origin;
    private Rect _viewport;

    public StatsDragGhostAdorner(UIElement host, ImageSource image, Size size, bool clipped, Brush accent)
        : base(host)
    {
        _image = image;
        _size = size;
        _clipped = clipped;
        _border = new Pen(accent, 1.5);
        _border.Freeze();
        IsHitTestVisible = false;
    }

    /// <summary>Renders the top of <paramref name="section"/> into a frozen bitmap at the screen's
    /// own scale; null when the section has no size yet.</summary>
    public static (ImageSource Image, Size Size, bool Clipped)? Snapshot(FrameworkElement section)
    {
        var width = section.ActualWidth;
        var height = Math.Min(section.ActualHeight, MaxGhostHeight);
        if (width < 1 || height < 1)
            return null;

        var dpi = VisualTreeHelper.GetDpi(section);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var brush = new VisualBrush(section)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height),
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return (bitmap, new Size(width, height), section.ActualHeight > MaxGhostHeight);
    }

    /// <summary>Places the picture's top left corner at <paramref name="origin"/>; both arguments
    /// are host coordinates.</summary>
    public void MoveTo(Point origin, Rect viewport)
    {
        _origin = origin;
        _viewport = viewport;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var rect = new Rect(_origin, _size);
        drawingContext.PushClip(new RectangleGeometry(_viewport));
        drawingContext.PushOpacity(GhostOpacity);
        if (_clipped)
        {
            var fade = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, rect.Bottom - FadeHeight),
                EndPoint = new Point(0, rect.Bottom),
            };
            fade.GradientStops.Add(new GradientStop(Colors.Black, 0));
            fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            drawingContext.PushOpacityMask(fade);
        }

        drawingContext.PushClip(new RectangleGeometry(rect, Radius, Radius));
        drawingContext.DrawImage(_image, rect);
        drawingContext.Pop();
        drawingContext.DrawRoundedRectangle(null, _border, rect, Radius, Radius);
        if (_clipped)
            drawingContext.Pop();
        drawingContext.Pop();
        drawingContext.Pop();
    }
}
