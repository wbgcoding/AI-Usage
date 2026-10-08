using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

/// <summary>Every class that touches one of this process's genuinely shared, non-thread-safe statics,
/// grouped into one xunit collection so xunit runs them one after another instead of side by side:
/// <list type="bullet">
/// <item>WPF's own <c>Application.Current</c> and its XAML/BAML parser - <c>RenderedTextContrastTests</c>,
/// <c>StatsWindowTests</c> and <c>AboutWindowTests</c> each build a real <c>AiUsage.App</c> on their own
/// dedicated STA thread; <c>ThemeContrastTests</c>, <c>ThemeGradientTests</c>, <c>ThemeTokenTests</c>,
/// <c>ResourceTypeTests</c>, <c>IconTests</c> and <c>SettingsViewModelTests</c> call
/// <c>XamlReader.Load</c> directly. WPF's markup parser keeps process-wide static caches that are not
/// safe to enter from two threads at once, real <c>Application</c> or not - the observed failure was a
/// spurious <c>ResourceReferenceKeyNotFoundException</c> for a key ("Accent") that is really there,
/// thrown only when another class's XAML parse overlapped it.</item>
/// <item><c>AiUsage.Services.LocalizationService.Instance</c> - every class that switches the active
/// language itself, or asserts on a string whose wording depends on whichever language happens to be
/// active right now.</item>
/// <item><c>AiUsage.Web.WebViewHost</c>'s own test-only root override - <c>CodexProviderTests</c>,
/// <c>GeminiProviderTests</c> and <c>WebViewHostTests</c> each point it at a fake profile root for the
/// duration of one test; two of them running at once let one silently steal the other's root, which
/// surfaced as a web-session-backed provider falling back to its signed-out numbers for no reason
/// visible in that test itself.</item>
/// <item><c>Application.Current</c> itself, read by production code that never touches a window -
/// <c>MainViewModelTests</c> exercises maintenance logic that marshals onto the UI thread only when
/// <c>Application.Current</c> is set; while one of this collection's own App-building classes has one
/// alive on its own worker thread, that check alone can send the call chasing a dispatcher instead of
/// running inline, so this class needs the same isolation despite never building a window itself.</item>
/// </list>
/// A class that touches more than one of the above joins this one collection rather than being split
/// across several: xunit allows a class only one <c>[Collection]</c>, and splitting them would have left
/// it protected against only one of its races.</summary>
[CollectionDefinition(Name)]
public class SharedStateTestsCollection : ICollectionFixture<TestPathsCleanupFixture>
{
    public const string Name = "AI-Usage shared-state tests";
}

/// <summary>Clears pooled SQLite connections once every test in <see cref="SharedStateTestsCollection"/>
/// has finished - a SQLite connection stays pooled, and its file handle open, past its own Dispose()
/// unless the pool is cleared first, which would otherwise leave every *.vscdb fixture the suite
/// created still locked. <see cref="TestPaths.Root"/> itself is no longer swept here: this fixture
/// disposes as soon as this one collection finishes, while other, parallel collections are still
/// creating and reading files under that same root, so a sweep at this point would delete a sibling
/// collection's files mid-test. The sweep instead runs once per process, before any test can touch
/// Root, in <see cref="TestPaths"/>'s own static constructor. Every other test class isolates itself
/// through <see cref="TestPaths.CreateDisposableDirectory"/>, and carries no collection attribute at
/// all.</summary>
public sealed class TestPathsCleanupFixture : IDisposable
{
    public void Dispose() => SqliteConnection.ClearAllPools();
}
