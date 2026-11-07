using System.Text;
using AiUsage.Io;

namespace AiUsage.Stats;

/// <summary>Which git repository a project folder belongs to: the normalized origin address, and the
/// repository's name (the last part of that address).</summary>
internal sealed record GitRepoInfo(string Id, string Name);

/// <summary>
/// Reads which git repository a folder is a checkout of, from the repository's config file alone
/// (git itself is never started, nothing is written into the folder). Two checkouts of one repository
/// share the normalized address of their <c>origin</c> remote. A <c>.git</c> file that points to
/// another git folder (a worktree or a submodule) is followed. A folder with no origin, or one that
/// is gone, has no identity.
/// </summary>
internal static class GitRepoIdentity
{
    private const int MaxConfigBytes = 256 * 1024;
    private const int MaxPointerBytes = 4096;

    /// <summary>The repository of <paramref name="folder"/>, or null when it is not a git checkout with an
    /// origin remote (or cannot be read). Touches the disk: call off the UI thread.</summary>
    public static GitRepoInfo? Read(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !GuardedRead.IsLocalDrivePath(folder))
                return null;

            var gitDirectory = FindGitDirectory(folder);
            if (gitDirectory is null)
                return null;

            var configPath = Path.Combine(CommonDirectory(gitDirectory) ?? gitDirectory, "config");
            if (!GuardedRead.TryReadSmallText(configPath, MaxConfigBytes, out var config))
                return null;

            var url = OriginUrl(config.Split('\n'));
            var id = url is null ? null : NormalizeRemote(url);
            return id is null ? null : new GitRepoInfo(id, RepoName(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The git folder of a checkout: <c>.git</c> itself, or where its <c>gitdir:</c> line points.</summary>
    private static string? FindGitDirectory(string folder)
    {
        var dotGit = Path.Combine(folder, ".git");
        if (Directory.Exists(dotGit))
            return dotGit;

        if (!GuardedRead.TryReadSmallText(dotGit, MaxPointerBytes, out var content))
            return null;

        var line = content.Split('\n').FirstOrDefault(text => text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
        var target = line?["gitdir:".Length..].Trim();
        return ExistingLocalDirectory(folder, target);
    }

    /// <summary>A worktree's git folder names the main one in <c>commondir</c>; the config lives there.</summary>
    private static string? CommonDirectory(string gitDirectory)
    {
        var pointer = Path.Combine(gitDirectory, "commondir");
        if (!GuardedRead.TryReadSmallText(pointer, MaxPointerBytes, out var content))
            return null;

        var target = content.Split('\n').FirstOrDefault()?.Trim();
        return ExistingLocalDirectory(gitDirectory, target);
    }

    /// <summary>The folder a pointer file names (absolute, or relative to <paramref name="baseFolder"/>),
    /// when it is on a local drive and exists. A target on a network share is dropped before any
    /// check touches it.</summary>
    private static string? ExistingLocalDirectory(string baseFolder, string? target)
    {
        if (string.IsNullOrEmpty(target))
            return null;

        var full = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(baseFolder, target));
        return GuardedRead.IsLocalDrivePath(full) && Directory.Exists(full) ? full : null;
    }

    /// <summary>The <c>url</c> of the <c>[remote "origin"]</c> section, or null.</summary>
    internal static string? OriginUrl(IEnumerable<string> configLines)
    {
        var inOrigin = false;
        foreach (var raw in configLines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;

            if (line[0] == '[')
            {
                var end = line.IndexOf(']');
                inOrigin = end > 0 && IsOriginHeader(line[1..end]);
                continue;
            }

            if (!inOrigin)
                continue;

            var equals = line.IndexOf('=');
            if (equals <= 0 || !line[..equals].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            return value.Length > 0 ? value : null;
        }

        return null;
    }

    private static bool IsOriginHeader(string header)
    {
        var parts = header.Trim().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && parts[0].Equals("remote", StringComparison.OrdinalIgnoreCase)
            && parts[1].Trim() == "\"origin\"";
    }

    /// <summary>One text for every spelling of a repository address: no scheme, no credentials, no port,
    /// lowercase host, no trailing slash and no <c>.git</c> (<c>git@Host:team/app.git</c> and
    /// <c>https://user:token@host/team/app/</c> both become <c>host/team/app</c>). Null for an empty address.</summary>
    internal static string? NormalizeRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var text = url.Trim().Replace('\\', '/');
        string host;
        string path;

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            var scheme = text[..schemeEnd];
            var rest = text[(schemeEnd + 3)..];
            var slash = rest.IndexOf('/');
            var authority = slash >= 0 ? rest[..slash] : rest;
            path = slash >= 0 ? rest[(slash + 1)..] : "";
            if (scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                return Finish("file", path);

            host = StripPort(StripUserInfo(authority));
        }
        else if (IsLocalPath(text))
        {
            return Finish("file", text);
        }
        else
        {
            // scp style: [user@]host:path
            var colon = text.IndexOf(':');
            if (colon <= 0)
                return null;

            host = StripUserInfo(text[..colon]);
            path = text[(colon + 1)..];
        }

        return host.Length == 0 ? null : Finish(host.ToLowerInvariant(), path);
    }

    private static string? Finish(string host, string path)
    {
        var trimmed = path.Trim().Trim('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4].TrimEnd('/');
        if (trimmed.Length == 0)
            return null;

        return host + "/" + trimmed;
    }

    private static string StripUserInfo(string authority)
    {
        var at = authority.LastIndexOf('@');
        return at >= 0 ? authority[(at + 1)..] : authority;
    }

    private static string StripPort(string hostAndPort)
    {
        if (hostAndPort.StartsWith('['))
        {
            var close = hostAndPort.IndexOf(']');
            return close > 0 ? hostAndPort[..(close + 1)] : hostAndPort;
        }

        var colon = hostAndPort.LastIndexOf(':');
        return colon >= 0 ? hostAndPort[..colon] : hostAndPort;
    }

    private static bool IsLocalPath(string text) =>
        text.StartsWith('/') || text.StartsWith('.')
        || (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':' && (text.Length == 2 || text[2] == '/'));

    /// <summary>The last part of a normalized address.</summary>
    internal static string RepoName(string id)
    {
        var slash = id.LastIndexOf('/');
        return slash >= 0 && slash < id.Length - 1 ? id[(slash + 1)..] : id;
    }
}
