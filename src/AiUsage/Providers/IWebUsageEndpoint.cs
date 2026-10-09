namespace AiUsage.Providers;

/// <summary>
/// The provider-specific half of a web-backed read: which script finds and re-reads the provider's
/// own usage endpoint, which paths that provider is ever allowed to ask for, and how its response
/// body turns into windows. <see cref="WebUsageSource"/> holds the provider-neutral other half
/// (discover once, cache the path, re-fetch it) and never names a provider itself.
/// </summary>
public interface IWebUsageEndpoint
{
    /// <summary>Script text that walks this provider's candidate paths and returns the first one
    /// answering with JSON, as the envelope <see cref="WebUsageSource.Interpret"/> reads.</summary>
    string Discover();

    /// <summary>Script text that re-reads one already-discovered path. Throws for a path outside
    /// <see cref="IsAllowedUsagePath"/>: it runs inside a signed-in session, so the caller must
    /// never hand it an unvalidated value.</summary>
    string Fetch(string path);

    /// <summary>The same read, leaving out the extra requests whose answers are already held (see
    /// <see cref="CachedAccountExtras"/>). Providers without such extras read exactly as before.</summary>
    string Fetch(string path, CachedAccountExtras cached) => Fetch(path);

    /// <summary>True only for one of this provider's own usage paths - the gate every cached or
    /// discovered path passes before it is stored or fetched again.</summary>
    bool IsAllowedUsagePath(string? path);

    /// <summary>Turns one response body into windows. Never throws: an unknown shape is a failure,
    /// never a guessed number.</summary>
    WebUsageResult ParseBody(string body);
}
