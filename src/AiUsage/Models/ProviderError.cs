namespace AiUsage.Models;

/// <summary>
/// Cause + action hint for a failed or degraded snapshot, never a raw exception. A provider that has
/// something more specific to say than the status' default text sets <see cref="ReasonKey"/> (and,
/// where one applies, <see cref="ActionKey"/>) to its own resource keys; <see cref="ErrorPresenter"/>
/// falls back to the status' own default wording otherwise. <see cref="DetailKey"/> names the cause
/// of a failed read (a resource key, with <see cref="DetailArg"/> filling its one placeholder, for
/// example an HTTP code); it is turned into text only when the tile resolves its wording, so a
/// language switch still applies.
/// </summary>
public sealed record ProviderError(string ReasonKey, string? ActionKey = null, string? DetailKey = null, string? DetailArg = null);
