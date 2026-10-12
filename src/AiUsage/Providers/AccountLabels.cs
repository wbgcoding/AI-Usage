using System.Globalization;

namespace AiUsage.Providers;

/// <summary>What an account label has to look like before it may reach the tile: every reader of an
/// account address (the Claude Code record, the Gemini account file, a web session answer) shares
/// these rules, so none of them lets through what another would refuse.</summary>
internal static class AccountLabels
{
    private const int MaxEmailLength = 254;

    /// <summary>True for a short text with exactly one '@' and only printable characters. Control and
    /// bidirectional characters are refused: the label reaches the screen, and such a character could
    /// reorder or hide what stands around it.</summary>
    internal static bool LooksLikeEmail(string? value)
    {
        if (value is not { Length: > 0 and <= MaxEmailLength })
            return false;

        var at = 0;
        foreach (var c in value)
        {
            if (c == '@')
                at++;
            if (!IsPrintable(c))
                return false;
        }

        return at == 1;
    }

    /// <summary>False for control, format (this includes the bidirectional marks and isolates),
    /// surrogate, private-use, unassigned and line or paragraph separator characters.</summary>
    internal static bool IsPrintable(char c) => char.GetUnicodeCategory(c) is not (
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
        or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
}
