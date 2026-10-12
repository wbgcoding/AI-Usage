using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>One kind of provider the app knows, as the About page and the statistics list it: its id,
/// its display name and the places it reads, in the current language. Built once by
/// <see cref="ProviderRegistry.Kinds"/>.</summary>
public sealed class ProviderKind
{
    private readonly IUsageProvider _display;

    internal ProviderKind(IUsageProvider display) => _display = display;

    public string Id => _display.Id;

    public string DisplayName => _display.DisplayName;

    /// <summary>Read afresh on every access, so a language switch shows in the new words.</summary>
    public IReadOnlyList<string> ReadLocations => _display.ReadLocations;
}
