namespace AiUsage.Models;

/// <summary>A window's used/total pair, when the provider's response carries one alongside the raw
/// percentage. <see cref="UnitKey"/> is a resource key naming what is being counted (requests,
/// messages, tokens).</summary>
public sealed record UsageAllowance(long Used, long Total, string UnitKey);

/// <summary>One percentage bar on a tile (the 5-hour window, the weekly window, or an "Other" one).</summary>
public sealed record UsageWindow
{
    /// <summary>Resource key, not the finished text - the tile resolves it through the active locale.</summary>
    public string Label { get; }

    public WindowKind Kind { get; }

    /// <summary>Clamped to 0-100 in the constructor: a provider's raw number is never trusted as-is.</summary>
    public double UsedPercent { get; }

    public DateTimeOffset? ResetsAt { get; }

    public int? WindowMinutes { get; }

    /// <summary>Optional real token count measured alongside this window; null
    /// when the provider has no local source for it.</summary>
    public TokenUsage? Tokens { get; }

    /// <summary>Optional used/total pair alongside the raw percentage; null when the provider's
    /// response carries no such denominator. Shown as the row's allowance text.</summary>
    public UsageAllowance? Allowance { get; }

    public UsageWindow(
        string label, WindowKind kind, double usedPercent, DateTimeOffset? resetsAt, int? windowMinutes,
        TokenUsage? tokens = null, UsageAllowance? allowance = null)
    {
        Label = label;
        Kind = kind;
        UsedPercent = Math.Clamp(usedPercent, 0, 100);
        ResetsAt = resetsAt;
        WindowMinutes = windowMinutes;
        Tokens = tokens;
        Allowance = allowance;
    }
}
