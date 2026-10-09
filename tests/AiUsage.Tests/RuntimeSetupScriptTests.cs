using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// The installer fetches Microsoft's .NET runtime through <c>installer/RuntimeSetup.ps1</c> and runs it,
/// so the gate in front of that run is pinned here: which addresses and signers pass, and that the
/// installer script wires the step in before any file of the app is written. The script is only ever
/// started in its check mode or dot-sourced; nothing here downloads or runs an installer.
/// </summary>
public class RuntimeSetupScriptTests
{
    // Split so the text scans for comments do not mistake the "//" of the address for a comment start.
    private const string ManualDownload = "https:" + "//dotnet.microsoft.com/download/dotnet/10.0";

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Script = Path.Combine(RepoRoot, "installer", "RuntimeSetup.ps1");
    private static readonly string InstallerScript = Path.Combine(RepoRoot, "installer", "AiUsage.iss");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find the repository root above " + AppContext.BaseDirectory);
    }

    private static (int ExitCode, string Output) RunPowerShell(params string[] arguments)
    {
        var start = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" }.Concat(arguments))
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "PowerShell did not finish in time.");
        return (process.ExitCode, output.GetAwaiter().GetResult().Trim());
    }

    private static string[] Evaluate(string expression)
    {
        var (code, output) = RunPowerShell("-Command", $". '{Script}'; {expression}");
        Assert.Equal(0, code);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [Fact]
    public void Download_addresses_must_be_https_on_a_microsoft_host_without_extras()
    {
        string[] allowed =
        [
            "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe",
            "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-10.0.12-win-x64.exe",
            "HTTPS://AKA.MS/dotnet/10.0/windowsdesktop-runtime-win-arm64.exe",
        ];
        string[] refused =
        [
            "http://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe",
            "https://aka.ms.example.com/runtime.exe",
            "https://example.com/aka.ms/runtime.exe",
            "https://user" + "@aka.ms/runtime.exe",
            "https://aka.ms:8443/runtime.exe",
            "ftp://aka.ms/runtime.exe",
            "file:///C:/runtime.exe",
            "//aka.ms/runtime.exe",
            "aka.ms/runtime.exe",
            "https://www.microsoft.com/runtime.exe",
        ];

        var cases = allowed.Concat(refused).Select(url => $"'{url}'");
        var answers = Evaluate($"foreach ($u in {string.Join(",", cases)}) {{ '{{0}}|{{1}}' -f $u, (Test-AllowedUrl $u) }}");

        Assert.Equal(allowed.Length + refused.Length, answers.Length);
        foreach (var url in allowed)
            Assert.Contains($"{url}|True", answers);
        foreach (var url in refused)
            Assert.Contains($"{url}|False", answers);
    }

    [Fact]
    public void Signer_must_be_the_organization_Microsoft_Corporation_and_nothing_that_only_contains_it()
    {
        // The real runtime installer is signed as CN=.NET with the company only as the organization.
        var cases = new (string Subject, bool Accepted)[]
        {
            ("CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", true),
            ("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", true),
            ("O=\"Microsoft Corporation\"", true),
            ("CN=Microsoft Corporation, O=Evil Ltd", false),
            ("CN=\"x, O=Microsoft Corporation\", O=Evil Ltd", false),
            ("O=Microsoft Corporation, O=Other", false),
            ("O=Microsoft Corporation Fake", false),
            ("O=microsoft corporation", false),
            ("CN=.NET", false),
            ("", false),
        };

        var list = string.Join(",", cases.Select(c => $"'{c.Subject.Replace("'", "''")}'"));
        var answers = Evaluate($"foreach ($s in {list}) {{ 'R|' + (Test-MicrosoftSubject $s) }}");

        Assert.Equal(cases.Select(c => "R|" + (c.Accepted ? "True" : "False")), answers);
    }

    [Fact]
    public void A_file_without_a_signature_is_refused()
    {
        // The test assembly itself is not signed.
        var unsigned = typeof(RuntimeSetupScriptTests).Assembly.Location;

        var (code, output) = RunPowerShell("-File", Script, "-VerifyFile", unsigned);

        Assert.Equal(12, code);
        Assert.Contains("NotSigned", output);
    }

    [Fact]
    public void A_file_that_is_missing_is_refused()
    {
        var (code, _) = RunPowerShell("-File", Script, "-VerifyFile", TestPaths.GetPath("no-such-runtime", ".exe"));

        Assert.NotEqual(0, code);
    }

    [Fact]
    public void A_genuine_Microsoft_signed_file_is_accepted()
    {
        // The .NET host that runs this test is signed by Microsoft the same way the runtime installer is.
        var dotnetRoot = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var host = Path.Combine(dotnetRoot, "dotnet.exe");
        if (!File.Exists(host) || !AiUsage.Services.NetworkStatus.HasInternet())
            return; // The check asks Microsoft's revocation servers, so it needs the host file and a connection.

        var (code, output) = RunPowerShell("-File", Script, "-VerifyFile", host);

        Assert.True(code == 0, $"expected the .NET host to pass, got {code}: {output}");
    }

    [Fact]
    public void A_missing_address_or_folder_stops_before_anything_is_downloaded()
    {
        var (code, _) = RunPowerShell("-File", Script, "-Url", "http://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe", "-Folder", TestPaths.CreateDirectory("runtime-folder"));

        Assert.Equal(10, code);
    }

    [Fact]
    public void The_script_stays_plain_ascii_so_every_Windows_PowerShell_reads_it_the_same()
    {
        var bytes = File.ReadAllBytes(Script);

        Assert.DoesNotContain(bytes, b => b > 127);
    }

    [Fact]
    public void The_installer_checks_the_runtime_in_the_registry_and_installs_it_before_any_file()
    {
        var text = File.ReadAllText(InstallerScript);

        Assert.Contains(@"SOFTWARE\dotnet\Setup\InstalledVersions\", text);
        Assert.Contains(@"\sharedfx\Microsoft.WindowsDesktop.App", text);
        Assert.Contains("https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-", text);
        Assert.Contains("HKLM32", text);
        Assert.Contains("function PrepareToInstall(var NeedsRestart: Boolean): String;", text);
        Assert.Contains("function ShouldSkipPage(PageID: Integer): Boolean;", text);
        Assert.Contains("Source: \"RuntimeSetup.ps1\"; Flags: dontcopy", text);
        // Never plain http for the download.
        Assert.DoesNotContain("http://", text);
    }

    [Fact]
    public void The_installer_texts_exist_in_both_languages_with_the_agreed_wording()
    {
        var text = File.ReadAllText(InstallerScript);

        foreach (var key in new[] { "RuntimeComponent", "RuntimeNeeded", "RuntimeInstalling", "RuntimeFailed" })
        {
            Assert.Matches($@"(?m)^en\.{key}=\S", text);
            Assert.Matches($@"(?m)^de\.{key}=\S", text);
        }

        Assert.Contains("en.RuntimeNeeded=AI-Usage needs the .NET 10 Desktop Runtime from Microsoft. Setup downloads and installs it now (about 60 MB).", text);
        Assert.Contains("de.RuntimeNeeded=AI-Usage braucht die .NET 10 Desktop Runtime von Microsoft. Das Setup l\u00e4dt sie jetzt herunter und installiert sie (etwa 60 MB).", text);
        Assert.Contains("en.RuntimeFailed=The .NET Desktop Runtime could not be installed. Install it from " + ManualDownload + " and start setup again.", text);
        Assert.Contains("de.RuntimeFailed=Die .NET Desktop Runtime lie\u00df sich nicht installieren. Installiere sie von " + ManualDownload + " und starte das Setup erneut.", text);
        Assert.DoesNotMatch(new Regex("[\u2013\u2014]"), text);
    }

    [Fact]
    public void The_build_publishes_without_a_bundled_runtime_and_ships_both_portables()
    {
        var build = File.ReadAllText(Path.Combine(RepoRoot, "build.bat"));

        Assert.Contains("--self-contained false", build);
        Assert.DoesNotMatch(new Regex(@"--self-contained\s*\^"), build);
        Assert.Contains("AI-Usage-%%a.exe", build);
        Assert.Contains("AI-Usage-arm64.exe", File.ReadAllText(Path.Combine(RepoRoot, "tools", "CheckFileVersions.ps1")));
    }
}
