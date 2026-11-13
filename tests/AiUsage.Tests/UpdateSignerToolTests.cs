using System.Diagnostics;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Runs the real release-signing tool against a key it makes for the test itself (never the
/// real signing key) and checks its output with the program's own verifier, so the two ends of the
/// signature format cannot drift apart.</summary>
// Both signer test classes start the tool with "dotnet run"; two builds of the same project at
// once collide on its output files, so they share one collection and run one after the other.
[Collection("UpdateSigner tool")]
public class UpdateSignerToolTests
{
    private static string ToolProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tools", "UpdateSigner")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("tools/UpdateSigner not found"), "tools", "UpdateSigner");
    }

    internal static (int ExitCode, string Output, string Error) Run(string arguments, string? keyFromEnvironment = null)
    {
        var start = new ProcessStartInfo("dotnet", $"run --project \"{ToolProject()}\" -c Release -- {arguments}")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        if (keyFromEnvironment is not null)
            start.Environment["AIUSAGE_SIGNING_KEY"] = keyFromEnvironment;

        using var process = Process.Start(start)!;
        // Both streams are read at once: a tool that fills one while the test waits on the other would hang.
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Trim(), error.Result.Trim());
    }

    /// <summary>Fails with what the tool (or the build in front of it) wrote, not just its exit code, so a
    /// failure on another machine says why.</summary>
    internal static void AssertSucceeded((int ExitCode, string Output, string Error) result) =>
        Assert.True(result.ExitCode == 0,
            $"The signing tool ended with exit code {result.ExitCode}.\nOutput:\n{Tail(result.Output)}\nErrors:\n{Tail(result.Error)}");

    private static string Tail(string text) => text.Length > 4000 ? text[^4000..] : text;

    [Fact]
    public void KeygenThenSignProducesASignatureTheProgramAccepts_AndNeverOverwritesAKey()
    {
        var folder = TestPaths.CreateDirectory("signer");
        var keyPath = Path.Combine(folder, "nested", "k.pem");
        var csPath = Path.Combine(folder, "UpdatePublicKey.cs");

        var generated = Run($"keygen --key \"{keyPath}\" --out-cs \"{csPath}\"");
        AssertSucceeded(generated);
        var publicKey = generated.Output;
        Assert.Contains(publicKey, File.ReadAllText(csPath), StringComparison.Ordinal);
        var keyBefore = File.ReadAllText(keyPath);

        var again = Run($"keygen --key \"{keyPath}\"");
        Assert.NotEqual(0, again.ExitCode);
        Assert.Equal(keyBefore, File.ReadAllText(keyPath));

        var file = Path.Combine(folder, "AI-Usage.exe");
        File.WriteAllBytes(file, [7, 7, 7, 1, 2, 3]);
        AssertSucceeded(Run($"sign --key \"{keyPath}\" \"{file}\""));

        var signature = File.ReadAllText(file + ".sig");
        Assert.True(UpdateSignature.Verify(File.ReadAllBytes(file), signature, publicKey));

        File.WriteAllBytes(file, [7, 7, 7, 1, 2, 4]);
        Assert.False(UpdateSignature.Verify(File.ReadAllBytes(file), signature, publicKey));

        // The environment variable names the key when no --key is given.
        File.WriteAllBytes(file, [5]);
        AssertSucceeded(Run($"sign \"{file}\"", keyFromEnvironment: keyPath));
        Assert.True(UpdateSignature.Verify([5], File.ReadAllText(file + ".sig"), publicKey));
    }

    [Fact]
    public void KeygenGivesOnlyTheCurrentUserAndSystemAccessToTheKey()
    {
        var keyPath = Path.Combine(TestPaths.CreateDirectory("signer-acl"), "k.pem");
        AssertSucceeded(Run($"keygen --key \"{keyPath}\""));

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
