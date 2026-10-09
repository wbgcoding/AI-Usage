namespace AiUsage.Models;

/// <summary>
/// Cause + action hint for a failed or degraded snapshot, never a raw exception. A provider that has
/// something more specific to say than the status' default text sets <see cref="ReasonKey"/> (and,
/// where one applies, <see cref="ActionKey"/>) to its own resource keys; <see cref="ErrorPresenter"/>
/// falls back to the status' own default wording otherwise. <see cref="DetailKey"/> names the cause
/// of a failed read (a resource key, with <see cref="DetailArg"/> filling its one placeholder, for
/// example an HTTP code); it is turned into text only when the tile resolves its wording, so a
/// language switch still applies. <see cref="Kind"/> and <see cref="HttpStatus"/> say what kind of
/// failure it was, so the tile can name it instead of a generic "could not read".
/// </summary>
public sealed record ProviderError(
    string ReasonKey, string? ActionKey = null, string? DetailKey = null, string? DetailArg = null,
    FailureKind Kind = FailureKind.Other, int? HttpStatus = null)
{
    /// <summary>The kind a bare HTTP status stands for: 5xx is the server's own problem, anything else
    /// a refusal; a status of 0 or below (no answer at all) says nothing and stays <see cref="FailureKind.Other"/>.</summary>
    public static FailureKind KindForStatus(int httpStatus) => httpStatus switch
    {
        >= 500 => FailureKind.ServerError,
        >= 100 => FailureKind.Refused,
        _ => FailureKind.Other,
    };
}

/// <summary>What kind of read failure a failed snapshot stands for: the provider's server answered
/// with an error, refused the request, did not answer in time, or the machine could not reach it.</summary>
public enum FailureKind
{
    Other,
    ServerError,
    Refused,
    Timeout,
    Network,
}
