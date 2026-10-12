namespace AiUsage.Tests;

/// <summary>Reads a source file of the checkout and cuts one member out of it, for the tests that
/// pin wiring in classes that cannot be instantiated without a live window.</summary>
internal static class SourceScan
{
    public static string Read(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine([RepoRoot.Find(), .. relativeParts]));

    /// <summary>The text from <paramref name="signature"/> up to the next line that closes a
    /// member at class level (four spaces, a brace) or the end of the file.</summary>
    public static string Member(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }
}
