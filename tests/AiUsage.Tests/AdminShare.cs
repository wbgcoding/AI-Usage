namespace AiUsage.Tests;

/// <summary>The same local file spelled as a UNC path through the machine's own administrative share
/// (<c>\\localhost\C$\...</c>), so a test can prove a UNC path is refused even though the target exists.</summary>
internal static class AdminShare
{
    /// <summary>The UNC spelling of <paramref name="localPath"/>, or null when this machine does not
    /// serve its administrative shares to the test (the UNC assertions then have nothing to prove).</summary>
    public static string? Of(string localPath)
    {
        var full = Path.GetFullPath(localPath);
        if (full.Length < 3 || full[1] != ':')
            return null;

        var unc = @"\\localhost\" + full[0] + "$" + full[2..];
        try
        {
            return Directory.Exists(unc) || File.Exists(unc) ? unc : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
