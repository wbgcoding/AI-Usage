using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Tests;

/// <summary>The display view of a provider: id, name and read locations pass straight through from the
/// provider it wraps, read afresh each time.</summary>
public class ProviderKindTests
{
    private sealed class StubProvider : IUsageProvider
    {
        public string Id { get; set; } = "stub";
        public string DisplayName { get; set; } = "Stub";
        public IReadOnlyList<string> ReadLocations { get; set; } = [];
        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void Id_and_display_name_come_from_the_wrapped_provider()
    {
        var kind = new ProviderKind(new StubProvider { Id = "codex", DisplayName = "Codex" });

        Assert.Equal("codex", kind.Id);
        Assert.Equal("Codex", kind.DisplayName);
    }

    [Fact]
    public void Read_locations_are_empty_when_the_provider_names_none()
    {
        Assert.Empty(new ProviderKind(new StubProvider()).ReadLocations);
    }

    [Fact]
    public void Read_locations_pass_through_in_order()
    {
        var kind = new ProviderKind(new StubProvider { ReadLocations = ["~/.codex/sessions", "~/.codex/auth"] });

        Assert.Equal(["~/.codex/sessions", "~/.codex/auth"], kind.ReadLocations);
    }

    [Fact]
    public void A_change_of_the_provider_shows_on_the_next_read()
    {
        var provider = new StubProvider { DisplayName = "Alt", ReadLocations = ["a"] };
        var kind = new ProviderKind(provider);
        Assert.Equal("Alt", kind.DisplayName);

        provider.DisplayName = "Neu";
        provider.ReadLocations = ["b"];

        Assert.Equal("Neu", kind.DisplayName);
        Assert.Equal(["b"], kind.ReadLocations);
    }

    [Fact]
    public void The_registry_lists_one_kind_per_known_provider_in_order()
    {
        Assert.Equal(AppSettings.KnownProviderIds, ProviderRegistry.Kinds.Select(kind => kind.Id));
    }
}
