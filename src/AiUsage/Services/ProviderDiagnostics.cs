using System.Globalization;

namespace AiUsage.Services;

/// <summary>
/// Builds the handful of lines a tile shows behind "Details" when a provider came back without
/// numbers: which folder was searched, how many files were looked at, how old the newest one is,
/// and what was found or missing. A provider that finds nothing must be able to say where it
/// looked, otherwise "nothing could be connected" has no surface that explains why.
///
/// Every path goes through <see cref="PathSanitizer"/> on the way in, so a screenshot or a pasted
/// copy of these lines never carries the user's home directory or account name.
/// </summary>
public sealed class ProviderDiagnostics
{
    private readonly List<string> _lines = [];
    private readonly Func<string, string> _text;

    /// <summary>The delegate is the test seam: unit tests pass the key straight through instead of
    /// depending on whichever language the service happens to be set to.</summary>
    public ProviderDiagnostics(Func<string, string>? text = null) =>
        _text = text ?? (key => LocalizationService.Instance[key]);

    public IReadOnlyList<string> Lines => _lines;

    public ProviderDiagnostics SearchedIn(string path) =>
        Add("Diag.SearchedIn", PathSanitizer.Sanitize(path));

    public ProviderDiagnostics DirectoryMissing() => Add("Diag.DirectoryMissing");

    public ProviderDiagnostics FilesChecked(int count) =>
        Add("Diag.FilesChecked", count.ToString(CultureInfo.CurrentCulture));

    /// <summary>Skipped rather than printed empty when no file was found at all.</summary>
    public ProviderDiagnostics NewestFile(DateTimeOffset? writtenAt) => writtenAt is { } stamp
        ? Add("Diag.NewestFile", stamp.LocalDateTime.ToString("g", CultureInfo.CurrentCulture))
        : this;

    public ProviderDiagnostics NothingFound() => Add("Diag.NothingFound");

    private ProviderDiagnostics Add(string key, string? argument = null)
    {
        var format = _text(key);
        _lines.Add(argument is null ? format : string.Format(CultureInfo.CurrentCulture, format, argument));
        return this;
    }
}
