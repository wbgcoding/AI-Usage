using System.Runtime.CompilerServices;
using AiUsage.Storage;

namespace AiUsage.Tests;

/// <summary>
/// Installs the one safety net every test in this assembly relies on without doing anything itself:
/// <see cref="AppPaths.DataDirectory"/> must never resolve to the real profile folder while any test
/// is running, no matter which test happens to be the first one to touch it, or which test clears its
/// own override afterwards (see <see cref="AppPaths.ClearOverrideForTests"/>). A module initializer
/// runs once, before any other code in this assembly - including a test class's own static state - so
/// this floor is already in place before the very first test method starts.
/// </summary>
internal static class TestAppPathsIsolation
{
    [ModuleInitializer]
    internal static void Install() =>
        AppPaths.EstablishOverrideFloorForTests(TestPaths.CreateDirectory("ai-usage-data-root"));
}

/// <summary>
/// <c>AIUSAGE_TEST_CULTURE=en-US</c> runs the whole suite with that regional format, as on the build
/// server, so a test that silently expects this machine's region fails here first.
/// </summary>
internal static class TestCultureOverride
{
    [ModuleInitializer]
    internal static void Install()
    {
        var name = Environment.GetEnvironmentVariable("AIUSAGE_TEST_CULTURE");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var culture = System.Globalization.CultureInfo.GetCultureInfo(name);
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
    }
}
