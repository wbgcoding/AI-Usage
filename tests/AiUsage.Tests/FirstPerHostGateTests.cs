using AiUsage.Web;

namespace AiUsage.Tests;

public class FirstPerHostGateTests
{
    [Fact]
    public void A_host_is_reported_once()
    {
        var gate = new FirstPerHostGate();

        Assert.True(gate.IsFirst("ads.example.com"));
        Assert.False(gate.IsFirst("ads.example.com"));
        Assert.False(gate.IsFirst("ADS.example.com"));
        Assert.True(gate.IsFirst("other.example.com"));
    }

    [Fact]
    public void Past_the_cap_no_new_host_is_reported()
    {
        var gate = new FirstPerHostGate();
        for (var i = 0; i < 20; i++)
            Assert.True(gate.IsFirst($"h{i}.example.com"));

        Assert.False(gate.IsFirst("h99.example.com"));
    }
}
