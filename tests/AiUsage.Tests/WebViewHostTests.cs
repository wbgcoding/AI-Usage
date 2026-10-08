using Microsoft.Web.WebView2.Core;
using AiUsage.Storage;
using AiUsage.Web;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class WebViewHostTests : IDisposable
{
    [Fact]
    public async Task Twenty_concurrent_calls_share_the_same_environment_creation_instead_of_starting_twenty()
    {
        var starts = 0;
        var gate = new TaskCompletionSource<CoreWebView2Environment>();
        var host = new WebViewHost(Track(TestPaths.GetPath("ai-usage-webview-env")), _ =>
        {
            Interlocked.Increment(ref starts);
            return gate.Task;
        });

        // Every caller races in before the one real creation this should ever start has finished -
        // a sign-in window and a background refresh reaching EnsureEnvironmentAsync at the same time,
        // twenty times over.
        var calls = Enumerable.Range(0, 20).Select(_ => host.EnsureEnvironmentAsync()).ToList();
        // CoreWebView2Environment has no public constructor - an uninitialized instance from the
        // runtime's own allocator stands in for it, since this test only needs one reference-equal
        // result, never a real, usable environment.
        gate.SetResult((CoreWebView2Environment)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CoreWebView2Environment)));
        await Task.WhenAll(calls);

        Assert.Equal(1, starts);
        Assert.All(calls, c => Assert.Same(calls[0].Result, c.Result));
    }

    [Fact]
    public void SignOut_deletes_only_the_widgets_own_session_folder_and_reports_success()
    {
        var root = Track(TestPaths.GetPath("ai-usage-webview"));
        var sessionFolder = Path.Combine(root, "webview");
        Directory.CreateDirectory(sessionFolder);
        File.WriteAllText(Path.Combine(sessionFolder, "cookie.dat"), "session-marker");
        var sibling = Path.Combine(root, "sibling.txt");
        File.WriteAllText(sibling, "must survive");
        var host = new WebViewHost(sessionFolder);

        var signedOut = host.SignOut();

        Assert.Equal(SignOutResult.Removed, signedOut);
        Assert.False(Directory.Exists(sessionFolder));
        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public void SignOut_reports_success_when_there_is_no_session_folder_yet()
    {
        var missing = Track(TestPaths.GetPath("ai-usage-webview"));
        var host = new WebViewHost(missing);

        var signedOut = host.SignOut();

        Assert.Equal(SignOutResult.Removed, signedOut);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void SignOut_reports_failure_when_a_file_inside_is_still_held_open()
    {
        var root = Track(TestPaths.GetPath("ai-usage-webview"));
        var sessionFolder = Path.Combine(root, "webview");
        Directory.CreateDirectory(sessionFolder);
        var lockedFile = Path.Combine(sessionFolder, "cookie.dat");
        File.WriteAllText(lockedFile, "session-marker");
        var host = new WebViewHost(sessionFolder);

        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var signedOut = host.SignOut();

            Assert.Equal(SignOutResult.StillOpen, signedOut);
            Assert.True(Directory.Exists(sessionFolder));
        }

        Directory.Delete(sessionFolder, recursive: true);
    }

    [Fact]
    public void ResolveUserDataFolder_of_an_empty_key_is_never_the_root_folder()
    {
        var root = Track(TestPaths.GetPath("ai-usage-webview-root"));
        WebViewHost.SetRootOverride(root);
        try
        {
            var resolved = WebViewHost.ResolveUserDataFolder(AppPaths.SanitizeAccountKeyForFileName(""));

            Assert.NotEqual(Path.GetFullPath(root), Path.GetFullPath(resolved));
        }
        finally
        {
            WebViewHost.ClearRootOverrideForTests();
        }
    }

    [Theory]
    [InlineData("claude#2")]
    [InlineData("claude#2/../../outside")]
    [InlineData("..\\..\\outside")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("..")]
    [InlineData("")]
    public void The_session_folder_of_any_account_key_is_a_direct_child_of_the_root(string accountKey)
    {
        var root = Track(TestPaths.GetPath("ai-usage-webview-root"));
        WebViewHost.SetRootOverride(root);
        try
        {
            var resolved = WebViewHost.ResolveUserDataFolder(AppPaths.SanitizeAccountKeyForFileName(accountKey));

            var parent = Path.GetDirectoryName(Path.GetFullPath(resolved));
            Assert.Equal(
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                parent!.TrimEnd(Path.DirectorySeparatorChar), ignoreCase: true);
        }
        finally
        {
            WebViewHost.ClearRootOverrideForTests();
        }
    }

    /// <summary>The guard in SignOut that refuses to delete the shared root folder can only ever fire
    /// through a resolved folder that equals the root. AppPaths.SanitizeAccountKeyForFileName is one
    /// layer keeping that from happening for a real account key; this proves the second, independent
    /// one inside WebViewHost itself holds even for a descriptor built with an empty profile folder
    /// name directly, bypassing that sanitizer.</summary>
    [Fact]
    public void ResolveUserDataFolder_of_a_descriptor_with_an_empty_profile_name_stays_below_the_root()
    {
        var root = Track(TestPaths.GetPath("ai-usage-webview-root"));
        WebViewHost.SetRootOverride(root);
        try
        {
            var descriptor = new WebSessionDescriptor(
                "test", "https://example.test/usage", "https://example.test/login", [], "");

            var resolved = WebViewHost.ResolveUserDataFolder(descriptor.ProfileFolderName);

            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var resolvedFull = Path.GetFullPath(resolved).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.NotEqual(rootFull, resolvedFull);
            Assert.StartsWith(rootFull + Path.DirectorySeparatorChar, resolvedFull);
        }
        finally
        {
            WebViewHost.ClearRootOverrideForTests();
        }
    }

    [Fact]
    public void SignOut_refuses_to_delete_the_shared_root_folder()
    {
        var root = Track(TestPaths.GetPath("ai-usage-webview-root"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "marker.txt");
        File.WriteAllText(marker, "must survive");
        WebViewHost.SetRootOverride(root);
        try
        {
            var host = new WebViewHost(root);

            var signedOut = host.SignOut();

            Assert.Equal(SignOutResult.Refused, signedOut);
            Assert.True(Directory.Exists(root));
            Assert.True(File.Exists(marker));
        }
        finally
        {
            WebViewHost.ClearRootOverrideForTests();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MigrateFlatProfileIfNeeded_moves_an_old_flat_profiles_content_into_the_named_subfolder()
    {
        var root = Track(TestPaths.GetPath("ai-usage-webview"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "cookie.dat"), "session-marker");
        Directory.CreateDirectory(Path.Combine(root, "EBWebView"));

        WebViewHost.MigrateFlatProfileIfNeeded(root, "claude");

        var target = Path.Combine(root, "claude");
        Assert.True(Directory.Exists(target));
        Assert.True(File.Exists(Path.Combine(target, "cookie.dat")));
        Assert.True(Directory.Exists(Path.Combine(target, "EBWebView")));
        Assert.False(File.Exists(Path.Combine(root, "cookie.dat")));
    }

    [Fact]
    public void MigrateFlatProfileIfNeeded_touches_nothing_once_the_root_already_has_per_provider_subfolders()
    {
        var root = Track(TestPaths.GetPath("ai-usage-webview"));
        var target = Path.Combine(root, "claude");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "cookie.dat"), "already-migrated");

        WebViewHost.MigrateFlatProfileIfNeeded(root, "claude");

        Assert.True(File.Exists(Path.Combine(target, "cookie.dat")));
        Assert.Equal(["cookie.dat"], Directory.GetFileSystemEntries(target).Select(Path.GetFileName));
        Assert.Equal([target], Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void MigrateFlatProfileIfNeeded_never_moves_another_accounts_profile_folder()
    {
        var root = Track(TestPaths.GetPath("ai-usage-webview"));
        var other = Path.Combine(root, "claude-2");
        Directory.CreateDirectory(Path.Combine(other, "EBWebView"));

        WebViewHost.MigrateFlatProfileIfNeeded(root, "claude-3");

        Assert.True(Directory.Exists(Path.Combine(other, "EBWebView")));
        Assert.False(Directory.Exists(Path.Combine(root, "claude-3")));
        Directory.Delete(root, recursive: true);
    }

    /// <summary>A flat profile root predates every provider but the first one, so a second provider
    /// resolving its own folder must never inherit that sign-in - its own sign-out would then delete
    /// someone else's session.</summary>
    [Fact]
    public void MigrateFlatProfileIfNeeded_leaves_a_flat_profile_to_the_provider_it_belonged_to()
    {
        var root = Track(TestPaths.GetPath("ai-usage-webview"));
        Directory.CreateDirectory(Path.Combine(root, "EBWebView"));
        File.WriteAllText(Path.Combine(root, "Local State"), "{}");

        WebViewHost.MigrateFlatProfileIfNeeded(root, "codex");

        Assert.False(Directory.Exists(Path.Combine(root, "codex")));
        Assert.True(File.Exists(Path.Combine(root, "Local State")));
        Directory.Delete(root, recursive: true);
    }

    private readonly List<string> _cleanupPaths = [];

    private string Track(string path)
    {
        _cleanupPaths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _cleanupPaths)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
