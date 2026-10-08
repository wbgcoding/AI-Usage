namespace AiUsage.Services;

/// <summary>
/// The one writer for a text field in an exported CSV file: RFC 4180 quoting plus a guard against
/// spreadsheet formulas, so a label that happens to start with "=" is shown as text and never run.
/// Numbers are written by the callers themselves, unquoted and in the invariant format.
/// </summary>
internal static class CsvField
{
    public static string Escape(string value)
    {
        if (value.Length == 0)
            return value;

        // A leading apostrophe is the spreadsheet convention for "this cell is text".
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
