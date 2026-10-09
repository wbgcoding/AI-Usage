using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>The corrected (ClickThrough, WindowLayer, WindowOpacityPercent) triple after switching
/// click-through on or off. ClickThrough itself is always echoed back unchanged - it is the input
/// that decides the other two, never something this policy itself would flip.</summary>
public readonly record struct ClickThroughResolution(bool ClickThrough, string WindowLayer, double WindowOpacityPercent);

/// <summary>Resolves the window-level/opacity side effects of switching click-through on or off.
/// One instance per window: it remembers, only for as long as click-through stays on, whichever
/// opacity was active right before it was switched on, so switching it back off can put that value
/// back. Stateful on purpose - see <see cref="Resolve"/> for why a call still counts as pure given
/// the same history of prior calls, and is proven that way below rather than only eyeballed once by
/// hand.</summary>
public sealed class ClickThroughPolicy
{
    /// <summary>A fully opaque window that also ignores the mouse is a bug report waiting to happen -
    /// the whole point of click-through is to see what sits behind the widget.</summary>
    public const double FallbackOpacityPercent = 85;

    private double? _opacityBeforeClickThrough;
    private double? _opacityLastAppliedForClickThrough;

    /// <summary>Click-through implies the "on top" window level (an overlay hidden behind other windows is
    /// pointless) and implies an opacity below 100 (see <see cref="FallbackOpacityPercent"/>).
    /// Switching click-through back off restores whatever opacity was active right before it was
    /// switched on - unless <paramref name="opacity"/> no longer matches the value this policy set
    /// while click-through was on, which means the user changed it themselves in the meantime, so
    /// that later, explicit choice is kept instead.</summary>
    public ClickThroughResolution Resolve(bool clickThrough, string windowLayer, double opacity)
    {
        if (clickThrough)
        {
            _opacityBeforeClickThrough ??= opacity;
            var resolvedOpacity = opacity >= 100 ? FallbackOpacityPercent : opacity;
            _opacityLastAppliedForClickThrough = resolvedOpacity;
            return new ClickThroughResolution(clickThrough, WindowLayers.OnTop, resolvedOpacity);
        }

        var userChangedOpacityWhileClickThroughWasOn =
            _opacityLastAppliedForClickThrough is { } lastApplied && opacity != lastApplied;
        var restoredOpacity = !userChangedOpacityWhileClickThroughWasOn && _opacityBeforeClickThrough is { } previous
            ? previous
            : opacity;

        _opacityBeforeClickThrough = null;
        _opacityLastAppliedForClickThrough = null;
        return new ClickThroughResolution(clickThrough, windowLayer, restoredOpacity);
    }
}
