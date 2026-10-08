using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// Picks the one snapshot a provider actually reports, out of every candidate source it managed to
/// read this tick. Every provider hands this its own candidates (a list of one is fine) instead of
/// returning whichever source happened to answer first. The five rules below are applied in this
/// exact order, so no provider carries its own judgement about which source to trust.
/// </summary>
public static class SnapshotChooser
{
    // How cheap a source is to ask again, cheapest first - the tie-break once two candidates are
    // otherwise equal. A kind no rule names explicitly (no provider offers one today) sorts after
    // every named one, so a future provider never silently jumps the queue.
    private static readonly Dictionary<SourceKind, int> Cost = new()
    {
        [SourceKind.LocalFile] = 0,
        [SourceKind.LocalLogin] = 1,
        [SourceKind.WebSession] = 2,
        [SourceKind.LocalDatabase] = 3,
        [SourceKind.None] = 4,
    };

    public static ProviderSnapshot Pick(IReadOnlyList<ProviderSnapshot> candidates)
    {
        if (candidates.Count == 0)
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));
        if (candidates.Count == 1)
            return candidates[0];

        var usable = candidates.Where(IsUsable).ToList();
        if (usable.Count > 0)
            return usable
                .OrderByDescending(candidate => candidate.Windows.Count)
                .ThenByDescending(candidate => candidate.DataTimestamp ?? DateTimeOffset.MinValue)
                .ThenBy(candidate => Cost[candidate.SourceKind])
                .First();

        // Nothing usable: the state that explains itself (asking to sign in) beats one that merely
        // failed, so the tile points at the actual problem instead of a generic error.
        return candidates
            .OrderBy(candidate => candidate.Status == ProviderStatus.NotSignedIn ? 0 : 1)
            .ThenBy(candidate => Cost[candidate.SourceKind])
            .First();
    }

    private static bool IsUsable(ProviderSnapshot snapshot) =>
        snapshot.Status == ProviderStatus.Ok && snapshot.Windows.Count > 0;
}
