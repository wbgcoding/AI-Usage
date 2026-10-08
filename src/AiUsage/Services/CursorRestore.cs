using System.Windows.Input;

namespace AiUsage.Services;

/// <summary>
/// Puts the pointer back under WPF's control. A sign-in page can leave the thread's current cursor
/// as it last asked for it (a page may request no cursor at all), and the page is a native child
/// window WPF never hears about: hiding it for a notice leaves that request standing for every
/// window of the app until something sets a cursor again. Clearing any override and forcing an
/// update makes WPF pick the cursor of whatever is under the pointer right now.
/// </summary>
internal static class CursorRestore
{
    /// <summary>Shows <paramref name="cursor"/> everywhere for the length of a drag; every caller
    /// ends it with <see cref="Reset"/>.</summary>
    internal static void Override(Cursor cursor) => Mouse.OverrideCursor = cursor;

    internal static void Reset()
    {
        Mouse.OverrideCursor = null;
        Mouse.UpdateCursor();
    }
}
