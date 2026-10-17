namespace AiUsage.Tests;

/// <summary>
/// A provider's answer changes without notice, so every file that parses a provider's answer has to
/// say what it no longer recognises (a <c>ChangeNotes</c> function of its own, or a report through
/// <c>ReadChangeLog</c>). A parser or endpoint added later without it fails here instead of going
/// silent the day the answer changes. The check is a pure function over text, proven against planted
/// violations below.
/// </summary>
public class ChangeNoteCoverageTests
{
    // Files in the scanned places that parse nothing provider specific. Anything else must report.
    private static readonly string[] Exempt =
    [
        // The interface every endpoint implements: declares the contract, holds no parsing at all.
        "IWebUsageEndpoint.cs",
    ];

    private static readonly string ProvidersDirectory = Path.Combine(RepoRoot.Find(), "src", "AiUsage", "Providers");

    private static IEnumerable<string> ScannedFiles() =>
        Directory.GetFiles(Path.Combine(ProvidersDirectory, "Parsing"), "*Parser.cs")
            .Concat(Directory.GetFiles(ProvidersDirectory, "*UsageEndpoint.cs"));

    /// <summary>True when the code (comments do not count) names <c>ChangeNotes</c> or <c>ReadChangeLog</c>.</summary>
    internal static bool ReportsChanges(string source)
    {
        var code = string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        return code.Contains("ChangeNotes", StringComparison.Ordinal) || code.Contains("ReadChangeLog", StringComparison.Ordinal);
    }

    [Fact]
    public void Every_parser_and_endpoint_reports_what_it_does_not_recognise()
    {
        var files = ScannedFiles().ToList();
        Assert.NotEmpty(files);

        var silent = files
            .Where(file => !Exempt.Contains(Path.GetFileName(file)))
            .Where(file => !ReportsChanges(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(silent.Count == 0,
            "These files parse a provider's answer without reporting what they do not recognise "
            + "(add a ChangeNotes function and report it through ReadChangeLog): " + string.Join(", ", silent));
    }

    [Fact]
    public void The_exemption_list_holds_only_files_that_exist_in_the_scanned_places()
    {
        var names = ScannedFiles().Select(Path.GetFileName).ToList();

        Assert.All(Exempt, exempt => Assert.Contains(exempt, names));
    }

    [Fact]
    public void A_file_that_mentions_the_report_only_in_a_comment_still_counts_as_silent() =>
        Assert.False(ReportsChanges("// see ReadChangeLog\n/// ChangeNotes\nclass X { }"));

    [Fact]
    public void A_file_with_a_change_notes_function_or_a_report_counts_as_reporting()
    {
        Assert.True(ReportsChanges("class X { public static string[] ChangeNotes(string json) => []; }"));
        Assert.True(ReportsChanges("class X { void M() => ReadChangeLog.Report(\"X\", \"note\"); }"));
    }
}
