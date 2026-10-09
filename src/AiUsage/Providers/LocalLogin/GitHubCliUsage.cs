using System.Diagnostics;

namespace AiUsage.Providers.LocalLogin;

/// <summary>How a GitHub CLI usage read ended.</summary>
internal enum GitHubCliOutcome
{
    Ok,
    NotSignedIn,
    ToolMissing,
    Failed,
}

internal sealed record CopilotFetch(GitHubCliOutcome Outcome, string Json)
{
    public static readonly CopilotFetch NotSignedIn = new(GitHubCliOutcome.NotSignedIn, "");
    public static readonly CopilotFetch ToolMissing = new(GitHubCliOutcome.ToolMissing, "");
    public static readonly CopilotFetch Failed = new(GitHubCliOutcome.Failed, "");
}

/// <summary>One GitHub CLI account <c>gh auth status</c> lists - which host, which login, and
/// whether it is the CLI's own currently active one for that host (the login a plain <c>gh api</c>
/// call without an explicit token reads as).</summary>
internal sealed record GitHubAccount(string Host, string Login, bool Active);

/// <summary>
/// Reads GitHub Copilot's quota through the GitHub CLI the user has already signed in on this
/// machine: <c>gh api copilot_internal/user</c>. The widget never touches the CLI's token - it only
/// asks the tool the user already trusts, which reads usage numbers only. Never throws.
/// </summary>
internal static class GitHubCliUsage
{
    public static Task<CopilotFetch> FetchAsync(CancellationToken ct) => FetchAsync(login: null, ct);

    /// <summary>Whether <c>gh.exe</c> is on PATH at all - the same check <see cref="FetchAsync"/>
    /// itself makes before ever running the tool, exposed separately so the sign-in button can offer
    /// the install page instead of trying (and failing) a read first.</summary>
    public static bool IsCliInstalled() => FindOnPath("gh.exe") is not null;

    /// <summary>The primary account's own read (<paramref name="login"/> null) asks <c>gh</c> as its
    /// own currently active user, exactly as before. A further account's read
    /// (<paramref name="login"/> set, from <see cref="ListSignedInUsersAsync"/>'s own list) first
    /// asks <c>gh auth token --user &lt;login&gt;</c> for that specific user's token, kept only in
    /// memory for the one request below, then reads the same endpoint with it - never logged, never
    /// written to a file, and never the currently active user's own credential.</summary>
    public static async Task<CopilotFetch> FetchAsync(string? login, CancellationToken ct)
    {
        if (FindOnPath("gh.exe") is not { } ghPath)
            return CopilotFetch.ToolMissing;

        string[] arguments = ApiArguments;
        Dictionary<string, string>? environment = null;
        if (login is not null)
        {
            var (tokenOutcome, token) = await RunGhAsync(ghPath, TokenArguments(login), ct);
            if (tokenOutcome != GitHubCliOutcome.Ok || string.IsNullOrWhiteSpace(token))
                return tokenOutcome switch
                {
                    GitHubCliOutcome.ToolMissing => CopilotFetch.ToolMissing,
                    GitHubCliOutcome.Failed => CopilotFetch.Failed,
                    _ => CopilotFetch.NotSignedIn,
                };

            environment = TokenEnvironment(token);
        }

        var (outcome, output) = await RunGhAsync(ghPath, arguments, ct, environment: environment);
        return outcome == GitHubCliOutcome.Ok ? new CopilotFetch(GitHubCliOutcome.Ok, output) : new CopilotFetch(outcome, "");
    }

    /// <summary>The token request for one listed account. The host is named, since only github.com
    /// accounts are listed and a CLI signed in to further hosts cannot tell which one a bare user means.</summary>
    internal static string[] TokenArguments(string login) => ["auth", "token", "--hostname", GitHubHost, "--user", login];

    internal const string GitHubHost = "github.com";

    /// <summary>The argument list of the usage read itself; a further account's token is never part of it.</summary>
    internal static readonly string[] ApiArguments = ["api", "copilot_internal/user"];

    /// <summary>The child process environment that carries a further account's token. It travels as
    /// <c>GH_TOKEN</c>, never in the argument list, where any process listing could read it.</summary>
    internal static Dictionary<string, string> TokenEnvironment(string token) =>
        new() { ["GH_TOKEN"] = token.Trim() };

    /// <summary>Every GitHub account the CLI is currently signed into, across every host, parsed from
    /// <c>gh auth status</c>'s own human-readable text (see <see cref="ParseAuthStatus"/> - there is
    /// no <c>--json</c> form of this particular command). Empty, never a thrown exception, when the
    /// tool is missing or nothing is signed in.</summary>
    public static async Task<IReadOnlyList<GitHubAccount>> ListSignedInUsersAsync(CancellationToken ct)
    {
        if (FindOnPath("gh.exe") is not { } ghPath)
            return [];

        var (outcome, output) = await RunGhAsync(ghPath, ["auth", "status"], ct, combineStreams: true);
        return outcome is GitHubCliOutcome.Ok or GitHubCliOutcome.NotSignedIn ? OnlyGitHubDotCom(ParseAuthStatus(output)) : [];
    }

    /// <summary>Copilot's quota lives on github.com; an account on any other host (an enterprise
    /// server) has none to read and is not offered.</summary>
    internal static IReadOnlyList<GitHubAccount> OnlyGitHubDotCom(IReadOnlyList<GitHubAccount> accounts) =>
        accounts.Where(account => string.Equals(account.Host, GitHubHost, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Pure parsing of <c>gh auth status</c>'s own text - one line per account, shaped
    /// "&#160;&#160;✓ Logged in to github.com account octocat (keyring)" (spelled with a leading
    /// checkmark and worded slightly differently across CLI versions, hence the loose match), with the
    /// active one for its host carrying a further "Active account: true" line right after it. Never
    /// throws: an unrecognised line is simply not an account.</summary>
    internal static IReadOnlyList<GitHubAccount> ParseAuthStatus(string text)
    {
        var accounts = new List<GitHubAccount>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var loginPattern = new System.Text.RegularExpressions.Regex(
            @"Logged in to (?<host>\S+) account (?<login>\S+)", System.Text.RegularExpressions.RegexOptions.Compiled);

        for (var i = 0; i < lines.Length; i++)
        {
            var match = loginPattern.Match(lines[i]);
            if (!match.Success)
                continue;

            var host = match.Groups["host"].Value;
            var login = match.Groups["login"].Value;
            // The active-account marker is one of the next couple of lines, indented further under
            // this same account block - never on the account's own line.
            var active = false;
            for (var j = i + 1; j < lines.Length && j < i + 4; j++)
            {
                if (loginPattern.IsMatch(lines[j]))
                    break;
                if (lines[j].Contains("Active account: true", StringComparison.OrdinalIgnoreCase))
                {
                    active = true;
                    break;
                }
            }

            accounts.Add(new GitHubAccount(host, login, active));
        }

        return accounts;
    }

    internal static async Task<(GitHubCliOutcome Outcome, string Output)> RunGhAsync(
        string ghPath, string[] arguments, CancellationToken ct, bool combineStreams = false,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(ghPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
                startInfo.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // gh is not installed or not on PATH.
            return (GitHubCliOutcome.ToolMissing, "");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            // gh auth status writes its whole report to stderr, not stdout - the caller asks for both
            // combined rather than guessing which stream a given CLI version chose.
            if (combineStreams)
                return (GitHubCliOutcome.Ok, stdout + "\n" + stderr);

            if (process.ExitCode == 0 && stdout.TrimStart().StartsWith('{'))
                return (GitHubCliOutcome.Ok, stdout);
            if (process.ExitCode == 0)
                return (GitHubCliOutcome.Ok, stdout.Trim());

            return (ClassifyFailure(stdout + "\n" + stderr), "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (GitHubCliOutcome.Failed, "");
        }
        finally
        {
            // Also on the caller's own cancellation (shutdown, sign-out): gh must not outlive the fetch.
            TryKill(process);
            process.Dispose();
        }
    }

    /// <summary>gh prints an auth hint when the user is not logged in, and the API answers 401 for a
    /// dead token; anything else that fails (a rate limit, a network error, a message that merely
    /// mentions authentication) is a transient failure, not a sign-in problem.</summary>
    internal static GitHubCliOutcome ClassifyFailure(string output) =>
        output.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
        || output.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase)
            ? GitHubCliOutcome.NotSignedIn
            : GitHubCliOutcome.Failed;

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (!Path.IsPathFullyQualified(directory))
                continue;
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry - skip it.
            }
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}
