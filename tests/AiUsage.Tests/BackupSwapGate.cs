namespace AiUsage.Tests;

/// <summary>Serializes the one test that sets the process-wide <c>BackupService.BeforeSwapMove</c> hook
/// against every other test that runs a restore, so neither needs a serial collection.</summary>
internal static class BackupSwapGate
{
    public static readonly object Lock = new();
}
