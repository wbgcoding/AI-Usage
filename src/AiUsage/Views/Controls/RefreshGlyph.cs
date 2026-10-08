using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AiUsage.Views.Controls;

/// <summary>One step of the refresh glyph's motion. <see cref="Angle"/> is unwrapped (it keeps
/// counting past 360), <see cref="Speed"/> is in degrees per second.</summary>
public readonly record struct RefreshSpinState(double Angle, double Speed)
{
    public static RefreshSpinState Rest => new(0, 0);

    public bool IsAtRest => Speed <= 0;
}

/// <summary>The motion model of <see cref="RefreshGlyph"/> as pure functions, so it is testable
/// without a window: a soft start to full speed, and a stop that keeps its speed until the braking
/// distance equals the distance left to the next multiple of 360 degrees, then brakes firmly to rest
/// exactly there. The glyph has a single arrow head, so only whole turns look upright.</summary>
public static class RefreshSpin
{
    public const double MaxSpeed = 420;
    public const double RampSeconds = 0.25;

    // The braking rate of the stop. Firm enough that a full-speed glyph is still within about a
    // second of a stop request in the worst case (the glyph has to cover up to one more turn first).
    private const double Deceleration = 1500;

    // Absorbs float drift so a target already reached by braking is not mistaken for the next one.
    private const double TargetTolerance = 1e-6;

    public static RefreshSpinState Step(RefreshSpinState state, double seconds, bool spinning)
    {
        if (seconds <= 0)
            return state;

        if (spinning)
        {
            // A rest after a finished stop sits on a multiple of 360; a new start counts from 0.
            var angle = state.IsAtRest ? 0 : state.Angle;
            var elapsed = Math.Sqrt(state.Speed / MaxSpeed) * RampSeconds;
            var next = Math.Min(elapsed + seconds, RampSeconds) / RampSeconds;
            var speed = MaxSpeed * next * next;
            return new RefreshSpinState(angle + (state.Speed + speed) / 2 * seconds, speed);
        }

        if (state.IsAtRest)
            return state;

        var brakingDistance = state.Speed * state.Speed / (2 * Deceleration);
        var target = Math.Ceiling((state.Angle + brakingDistance - TargetTolerance) / 360) * 360;
        var coastDistance = Math.Max(0, target - state.Angle - brakingDistance);

        // Keeps its speed until the braking distance is all that is left.
        if (coastDistance > state.Speed * seconds)
            return new RefreshSpinState(state.Angle + state.Speed * seconds, state.Speed);

        var coastTime = coastDistance / state.Speed;
        var brakeTime = seconds - coastTime;
        var slower = state.Speed - Deceleration * brakeTime;
        if (slower <= 0)
            return new RefreshSpinState(target, 0);

        var travelled = coastDistance + state.Speed * brakeTime - Deceleration * brakeTime * brakeTime / 2;
        return new RefreshSpinState(state.Angle + travelled, slower);
    }
}

/// <summary>The refresh icon, drawn on its own 24 by 24 grid so it turns around the middle of its
/// circle instead of the middle of its bounds (the arrow head makes those differ, which made the
/// old glyph wobble). Spins while <see cref="IsSpinning"/> is true: eases in, and when it stops it
/// coasts to a rest at its upright position instead of jumping back. Stays still when Windows has
/// animations turned off.</summary>
public sealed class RefreshGlyph : Viewbox
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(RefreshGlyph),
        new PropertyMetadata(null, (d, e) => ((RefreshGlyph)d)._path.Fill = (Brush?)e.NewValue));

    public static readonly DependencyProperty IsSpinningProperty = DependencyProperty.Register(
        nameof(IsSpinning), typeof(bool), typeof(RefreshGlyph),
        new PropertyMetadata(false, (d, _) => ((RefreshGlyph)d).Sync()));

    private readonly System.Windows.Shapes.Path _path = new() { Stretch = Stretch.None };
    private readonly RotateTransform _rotation = new();
    private RefreshSpinState _state = RefreshSpinState.Rest;
    private readonly Stopwatch _clock = new();
    private TimeSpan _lastTick;
    private bool _hooked;

    public RefreshGlyph()
    {
        _path.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "Icon.Refresh");
        Stretch = Stretch.Uniform;
        Child = new Canvas
        {
            Width = 24,
            Height = 24,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _rotation,
            Children = { _path },
        };

        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Unhook(reset: true);
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
                Unhook(reset: true);
            else
                Sync();
        };
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public bool IsSpinning
    {
        get => (bool)GetValue(IsSpinningProperty);
        set => SetValue(IsSpinningProperty, value);
    }

    private void Sync()
    {
        if (!IsSpinning || _hooked || !IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation)
            return;

        _hooked = true;
        _clock.Restart();
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Unhook(bool reset)
    {
        if (_hooked)
            CompositionTarget.Rendering -= OnRendering;

        _hooked = false;
        _clock.Stop();
        if (reset)
        {
            _state = RefreshSpinState.Rest;
            _rotation.Angle = 0;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var seconds = Math.Min((now - _lastTick).TotalSeconds, 0.1);
        _lastTick = now;

        _state = RefreshSpin.Step(_state, seconds, IsSpinning);
        _rotation.Angle = _state.Angle % 360;
        if (!IsSpinning && _state.IsAtRest)
            Unhook(reset: true);
    }
}
