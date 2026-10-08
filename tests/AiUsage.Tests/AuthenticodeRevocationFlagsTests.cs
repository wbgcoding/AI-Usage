using AiUsage.Web;

namespace AiUsage.Tests;

public class AuthenticodeRevocationFlagsTests
{
    // The Windows SDK values: reverting either to "no revocation check" (0 or 0x10) must fail here.
    [Fact]
    public void The_trust_check_asks_for_revocation_of_the_whole_chain()
    {
        Assert.Equal(1u, AuthenticodeSignature.WtdRevokeWholeChain);
        Assert.Equal(0x40u, AuthenticodeSignature.WtdRevocationCheckChain);
    }
}
