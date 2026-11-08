namespace AiUsage.Services;

/// <summary>The few ways a visible text is put together from parts, each one a resource so a language
/// can change the separator or the order (ja and zh-Hans have no space between a number and its unit
/// and use their own list comma).</summary>
public static class TextCompose
{
    private static LocalizationService Loc => LocalizationService.Instance;

    /// <summary>"{first} · {second}".</summary>
    public static string Pair(string first, string second) => Loc.Format("Text.Pair", first, second);

    /// <summary>"{name} ({detail})".</summary>
    public static string WithDetail(string name, string detail) => Loc.Format("Text.WithDetail", name, detail);

    /// <summary>"{number} {unit}".</summary>
    public static string NumberUnit(string number, string unit) => Loc.Format("Text.NumberUnit", number, unit);

    /// <summary>"{name} {item}": a provider name followed by what it is about (a window label).</summary>
    public static string NameAndItem(string name, string item) => Loc.Format("Text.NameAndItem", name, item);

    /// <summary>Two sentences, or a label and its figure, one after the other.</summary>
    public static string Sentences(string first, string second) => Loc.Format("Text.Sentences", first, second);

    /// <summary>The parts joined with the language's list separator; empty parts are skipped.</summary>
    public static string List(IEnumerable<string> parts) =>
        string.Join(Loc["Text.ListSeparator"], parts.Where(part => part.Length > 0));

    /// <summary>The parts joined with " · " as <see cref="Pair"/> does; empty parts are skipped.</summary>
    public static string PairAll(IEnumerable<string> parts)
    {
        var joined = "";
        foreach (var part in parts.Where(part => part.Length > 0))
            joined = joined.Length == 0 ? part : Pair(joined, part);
        return joined;
    }
}
