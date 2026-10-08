namespace AiUsage.Models;

/// <summary>A tile's state. Drives both what is shown and the tooltip text.</summary>
public enum ProviderStatus
{
    Ok,
    Stale,
    NoLocalData,
    SourceUnavailable,
    NotSignedIn,
    Blocked,
    RuntimeMissing,
    Failed,
}
