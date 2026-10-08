using System.Linq;
using System.Windows;

namespace AiUsage.Views;

/// <summary>
/// Resolves the window a newly created dialog should actually own itself to. WPF refuses to set
/// <c>Window.Owner</c> to a window that has never been shown (the setter throws
/// <see cref="InvalidOperationException"/>), which is exactly what happened when the welcome dialog -
/// shown before the main window's own first <c>Show()</c> call - handed out the still-unshown main
/// window as an owner. The preferred candidate only wins here once it is actually showing; otherwise
/// whichever window the application currently reports as active steps in, and if nothing is showing
/// at all the new dialog simply gets no owner rather than crash.
/// </summary>
internal static class OwnerWindowResolver
{
    /// <summary>
    /// The decision itself, generic so it is reachable from a test without a live <see
    /// cref="Application"/> or even a real <see cref="Window"/> instance - "shown" is passed in
    /// rather than derived here, which is what keeps this method pure.
    /// </summary>
    internal static T? Resolve<T>(T? preferred, bool preferredIsShown, T? active, bool activeIsShown)
        where T : class
    {
        if (preferred is not null && preferredIsShown)
            return preferred;

        if (active is not null && activeIsShown)
            return active;

        return null;
    }

    /// <summary>
    /// Thin wrapper around <see cref="Resolve{T}"/> for real callers: a window counts as "shown"
    /// once it has a live presentation source (set once WPF has actually shown it, cleared again once
    /// it closes), never a flag a caller has to remember to set, and the fallback is whichever window
    /// <see cref="Application.Current"/> currently reports as active.
    /// </summary>
    public static Window? ResolveOwner(Window? preferred)
    {
        var active = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return Resolve(preferred, IsShown(preferred), active, IsShown(active));
    }

    private static bool IsShown(Window? window) =>
        window is not null && PresentationSource.FromVisual(window) is not null;

    /// <summary>
    /// The decision behind <see cref="ApplyOwner"/>, generic for the same test-without-a-real-window
    /// reason as <see cref="Resolve{T}"/>: an unowned dialog needs to be switched to
    /// <see cref="WindowStartupLocation.CenterScreen"/> explicitly, since <c>CenterOwner</c> - what
    /// four of today's six dialogs declare in XAML - silently behaves like <c>Manual</c> (a top-left
    /// corner open) once there is no owner to center against.
    /// </summary>
    internal static bool NeedsCenterOnScreen<T>(T? resolvedOwner) where T : class => resolvedOwner is null;

    /// <summary>
    /// Sets both at once, replacing a plain <c>{ Owner = ... }</c> object initializer at every
    /// creation site: resolves a safe owner for <paramref name="dialog"/> and, when none exists,
    /// centers it on the screen instead of leaving it wherever <c>CenterOwner</c>'s Manual fallback
    /// would otherwise place it.
    /// </summary>
    public static void ApplyOwner(Window dialog, Window? preferred)
    {
        var owner = ResolveOwner(preferred);
        dialog.Owner = owner;
        if (NeedsCenterOnScreen(owner))
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }
}
