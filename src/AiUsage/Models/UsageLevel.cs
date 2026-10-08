namespace AiUsage.Models;

/// <summary>A bar's warning stage, derived from its percent. Colour is never
/// the only carrier of this: the percent text and, from Crit, a warning glyph always sit next to it.</summary>
public enum UsageLevel
{
    Ok,
    Warn,
    Crit,
}
