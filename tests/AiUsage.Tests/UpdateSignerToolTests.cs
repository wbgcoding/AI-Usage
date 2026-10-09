using System.Diagnostics;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Runs the real release-signing tool against a key it makes for the test itself (never the
/// real signing key) and checks its output with the program's own verifier, so the two ends of the
/// signature format cannot drift apart.</summary>
public class UpdateSignerToolTests
{
    private static string ToolProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tools", "UpdateSigner")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("tools/UpdateSigner not found"), "tools", "UpdateSigner");
    }

    private static (int ExitCode, string Output) Run(string arguments, string? keyFromEnvironment = null)
    {
        var start = new ProcessStartInfo("dotnet", $"run --project \"{ToolProject()}\" -c Release -- {arguments}")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        if (keyFromEnvironment is not null)
            start.Environment["AIUSAGE_SIGNING_KEY"] = keyFromEnvironment;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Trim());
    }

    [Fact]
    public void KeygenThenSignProducesASignatureTheProgramAccepts_AndNeverOverwritesAKey()
    {
        var folder = TestPaths.CreateDirectory("signer");
        var keyPath = Path.Combine(folder, "nested", "k.pem");
        var csPath = Path.Combine(folder, "UpdatePublicKey.cs");

        var generated = Run($"keygen --key \"{keyPath}\" --out-cs \"{csPath}\"");
        Assert.Equal(0, generated.ExitCode);
        var publicKey = generated.Output;
        Assert.Contains(publicKey, File.ReadAllText(csPath), StringComparison.Ordinal);
        var keyBefore = File.ReadAllText(keyPath);

        var again = Run($"keygen --key \"{keyPath}\"");
        Assert.NotEqual(0, again.ExitCode);
        Assert.Equal(keyBefore, File.ReadAllText(keyPath));

        var file = Path.Combine(folder, "AI-Usage.exe");
        File.WriteAllBytes(file, [7, 7, 7, 1, 2, 3]);
        Assert.Equal(0, Run($"sign --key \"{keyPath}\" \"{file}\"").ExitCode);

        var signature = File.ReadAllText(file + ".sig");
        Assert.True(UpdateSignature.Verify(File.ReadAllBytes(file), signature, publicKey));

        File.WriteAllBytes(file, [7, 7, 7, 1, 2, 4]);
        Assert.False(UpdateSignature.Verify(File.ReadAllBytes(file), signature, publicKey));

        // The environment variable names the key when no --key is given.
        File.WriteAllBytes(file, [5]);
        Assert.Equal(0, Run($"sign \"{file}\"", keyFromEnvironment: keyPath).ExitCode);
        Assert.True(UpdateSignature.Verify([5], File.ReadAllText(file + ".sig"), publicKey));
    }

    [Fact]
    public void KeygenGivesOnlyTheCurrentUserAndSystemAccessToTheKey()
    {
        var keyPath = Path.Combine(TestPaths.CreateDirectory("signer-acl"), "k.pem");
        Assert.Equal(0, Run($"keygen --key \"{keyPath}\"").ExitCode);

        var security = new FileInfo(keyPath).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var holders = security
            .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .Select(rule => rule.IdentityReference.Value)
            .Distinct()
            .Order()
            .ToArray();
        var expected = new[]
        {
            System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value,
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null).Value,
        }.Order().ToArray();
        Assert.Equal(expected, holders);
    }
}
