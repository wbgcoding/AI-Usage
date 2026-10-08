using System.Runtime.CompilerServices;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Every notice the app shows uses its own themed window: a framework message box opens
/// in the plain system style and looks foreign next to the rest of the app.</summary>
public class ThemedDialogTests
{
    [Fact]
    public void No_source_file_opens_a_framework_message_box()
    {
        var src = Path.Combine(RepoRoot(), "src", "AiUsage");
        var offenders = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("MessageBox.Show"))
            .Select(f => Path.GetFileName(f))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(here)))!;
}
