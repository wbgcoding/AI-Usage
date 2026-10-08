namespace AiUsage.Models;

/// <summary>Where a snapshot's numbers came from.</summary>
public enum SourceKind
{
    LocalFile,
    LocalDatabase,
    WebSession,
    // Read live through the sign-in the provider's own tool already holds on this machine (a token
    // in the OS credential store or a tool config file), used only in memory, never written back.
    LocalLogin,
    None,
}
