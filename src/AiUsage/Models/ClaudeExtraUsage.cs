namespace AiUsage.Models;

/// <summary>
/// The pay-as-you-go extra usage of a Claude plan for the current month: whether it is switched on,
/// how much of the monthly limit is used and the amounts in major units (dollars, not cents). Every
/// figure is null when the answer did not carry it - a missing number is never guessed.
/// </summary>
public sealed record ClaudeExtraUsage(bool Enabled, double? UsedPercent, decimal? Used, decimal? Limit, string Currency);
