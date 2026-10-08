using AiUsage.Providers;
using AiUsage.Services;

namespace AiUsage.Tests;

/// <summary>
/// A provider that comes back without numbers has to say where it looked, or "I see no data" has no
/// surface that explains it. These tests cover the two halves of that promise: every provider
/// produces the lines, and no line ever carries the user's own path.
/// </summary>
public class ProviderDiagnosticsTests
{
    // Deliberately never created: the point is the "that folder does not exist" case, and putting it
    // under the real profile is what makes the sanitising assertion below meaningful.
    private static string MissingFolderInTheUserProfile(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $"ai-usage-tests-no-such-folder-{name}");

    // Only the file-based providers produce diagnostic lines. Gemini and Copilot read live through a
    // signed-in tool now, with no folder to look in, so they are out of this contract by design.
    private static IUsageProvider Create(string providerId) => providerId switch
    {
        "codex" => new CodexProvider(MissingFolderInTheUserProfile("codex"), now: null),
        "claude" => new ClaudeProvider(MissingFolderInTheUserProfile("claude"), now: null),
        _ => throw new ArgumentOutOfRangeException(nameof(providerId), providerId, "Unknown provider."),
    };

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task A_provider_without_data_says_where_it_looked(string providerId)
    {
        var snapshot = await Create(providerId).FetchAsync(CancellationToken.None);

        Assert.Empty(snapshot.Windows);
        Assert.NotNull(snapshot.Diagnostics);
        Assert.NotEmpty(snapshot.Diagnostics!);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task No_diagnostic_line_carries_the_users_own_path(string providerId)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userName = Environment.UserName;

        var snapshot = await Create(providerId).FetchAsync(CancellationToken.None);
        var lines = snapshot.Diagnostics!;

        Assert.Contains(lines, line => line.Contains("<user>", StringComparison.Ordinal));
        foreach (var line in lines)
        {
            Assert.DoesNotContain(profile, line, StringComparison.OrdinalIgnoreCase);
            if (userName.Length > 2)
                Assert.DoesNotContain(userName, line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_builder_formats_one_line_per_call_and_skips_an_unknown_file_date()
    {
        var diagnostics = new ProviderDiagnostics(key => key switch
        {
            "Diag.SearchedIn" => "in {0}",
            "Diag.FilesChecked" => "checked {0}",
            "Diag.NothingFound" => "nothing",
            _ => key,
        });

        diagnostics.SearchedIn(@"C:\somewhere\else").FilesChecked(3).NewestFile(null).NothingFound();

        Assert.Equal([@"in C:\somewhere\else", "checked 3", "nothing"], diagnostics.Lines);
    }
}
