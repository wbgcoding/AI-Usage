using Xunit;

namespace AiUsage.Tests;

/// <summary>The release tool's <c>verify</c> command, run against keys it makes for the test itself
/// (never the real signing key): a signature made with the key behind the app's public key passes, one
/// made with any other key, a changed file or a missing signature fails.</summary>
public class UpdateSignerVerifyTests
{
    private static (string Folder, string KeyPath, string PublicKeySource) NewKey(string name)
    {
        var folder = TestPaths.CreateDirectory(name);
        var keyPath = Path.Combine(folder, name + ".pem");
        var source = Path.Combine(folder, name + "-UpdatePublicKey.cs");
        Assert.Equal(0, UpdateSignerToolTests.Run($"keygen --key \"{keyPath}\" --out-cs \"{source}\"").ExitCode);
        return (folder, keyPath, source);
    }

    private static string Signed(string folder, string keyPath)
    {
        var file = Path.Combine(folder, "AI-Usage.exe");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        Assert.Equal(0, UpdateSignerToolTests.Run($"sign --key \"{keyPath}\" \"{file}\"").ExitCode);
        return file;
    }

    [Fact]
    public void A_signature_made_with_the_key_behind_the_public_key_verifies()
    {
        var (folder, key, publicKey) = NewKey("verify-good");
        var file = Signed(folder, key);

        var result = UpdateSignerToolTests.Run($"verify --pubkey \"{publicKey}\" \"{file}\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Verified", result.Output);
    }

    [Fact]
    public void A_signature_from_another_key_fails()
    {
        var (folder, key, _) = NewKey("verify-signer");
        var (_, _, otherPublicKey) = NewKey("verify-other");
        var file = Signed(folder, key);

        Assert.NotEqual(0, UpdateSignerToolTests.Run($"verify --pubkey \"{otherPublicKey}\" \"{file}\"").ExitCode);
    }

    [Fact]
    public void A_changed_file_or_a_missing_signature_fails()
    {
        var (folder, key, publicKey) = NewKey("verify-changed");
        var file = Signed(folder, key);

        File.WriteAllBytes(file, [1, 2, 3, 5]);
        Assert.NotEqual(0, UpdateSignerToolTests.Run($"verify --pubkey \"{publicKey}\" \"{file}\"").ExitCode);

        File.Delete(file + ".sig");
        Assert.NotEqual(0, UpdateSignerToolTests.Run($"verify --pubkey \"{publicKey}\" \"{file}\"").ExitCode);
    }

    [Fact]
    public void Verify_without_a_public_key_source_is_refused()
    {
        var (folder, key, _) = NewKey("verify-usage");
        var file = Signed(folder, key);

        Assert.NotEqual(0, UpdateSignerToolTests.Run($"verify \"{file}\"").ExitCode);
    }

    [Fact]
    public void The_build_script_verifies_every_signed_asset_against_the_app_key()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot.Find(), "build.bat"));

        Assert.Contains("UpdateSigner.dll\" verify --pubkey \"%ROOT%src\\AiUsage\\Services\\UpdatePublicKey.cs\" \"%%f\"", script);
    }
}
