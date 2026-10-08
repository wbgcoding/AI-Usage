namespace AiUsage.Models;

/// <summary>
/// A token count read straight from a provider's own local file - never an
/// estimate, never derived from the percent value. Attached to the window it was measured
/// alongside; a provider that cannot honestly attribute a count to one window simply omits it.
/// </summary>
public sealed record TokenUsage(long TotalTokens);
