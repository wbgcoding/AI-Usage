using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>Two checkouts of one git repository count as one project; the address is read from the
/// repository's config file, never through git.</summary>
public sealed class GitRepoIdentityTests
{
    private static readonly DateOnly Day = new(2026, 10, 1);
    private readonly string _root = TestPaths.CreateDirectory("git-repo-identity");

    [Theory]
    [InlineData("https://github.com/Team/App.git", "github.com/Team/App")]
    [InlineData("https://GitHub.com/Team/App", "github.com/Team/App")]
    [InlineData("https://github.com/Team/App/", "github.com/Team/App")]
    [InlineData("https://github.com/Team/App.git/", "github.com/Team/App")]
    [InlineData("git@github.com:Team/App.git", "github.com/Team/App")]
    [InlineData("git@GITHUB.com:Team/App", "github.com/Team/App")]
    [InlineData("ssh://git@github.com/Team/App.git", "github.com/Team/App")]
    [InlineData("ssh://git@github.com:22/Team/App.git", "github.com/Team/App")]
    [InlineData("https://user:secret@github.com/Team/App.git", "github.com/Team/App")]
    [InlineData("https://token@github.com/Team/App", "github.com/Team/App")]
    [InlineData("  https://github.com/Team/App.git  ", "github.com/Team/App")]
    [InlineData(@"C:\repos\App.git", "file/C:/repos/App")]
    [InlineData("file:///C:/repos/App.git", "file/C:/repos/App")]
    public void Every_spelling_of_an_address_normalizes_alike(string url, string expected) =>
        Assert.Equal(expected, GitRepoIdentity.NormalizeRemote(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("https://github.com/")]
    public void An_address_without_a_repository_has_no_identity(string? url) =>
        Assert.Null(GitRepoIdentity.NormalizeRemote(url));

    [Fact]
    public void The_forms_of_one_address_are_equal_and_a_credential_never_stays()
    {
        var forms = new[]
        {
            "git@github.com:Team/App.git", "https://github.com/Team/App", "https://me:tok@GitHub.com/Team/App.git/",
        };

        Assert.Single(forms.Select(GitRepoIdentity.NormalizeRemote).Distinct());
        Assert.DoesNotContain("tok", GitRepoIdentity.NormalizeRemote(forms[2]));
        Assert.Equal("App", GitRepoIdentity.RepoName(GitRepoIdentity.NormalizeRemote(forms[0])!));
    }

    [Fact]
    public void The_origin_url_is_taken_from_the_origin_section_only()
    {
        string[] config =
        [
            "[core]", "\tbare = false", "[remote \"upstream\"]", "\turl = https://github.com/other/Fork.git",
            "# comment", "[remote \"origin\"]", "\turl = https://github.com/Team/App.git", "\tfetch = +refs/heads/*:refs/remotes/origin/*",
        ];

        Assert.Equal("https://github.com/Team/App.git", GitRepoIdentity.OriginUrl(config));
        Assert.Null(GitRepoIdentity.OriginUrl(["[remote \"upstream\"]", "url = https://x.test/a/b"]));
    }

    private string Checkout(string name, string? origin, string? sectionHeader = null)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        File.WriteAllText(Path.Combine(folder, ".git", "config"), Config(origin, sectionHeader));
        return folder;
    }

    private static string Config(string? origin, string? sectionHeader = null) =>
        "[core]\n\tbare = false\n" + (origin is null ? "" : $"{sectionHeader ?? "[remote \"origin\"]"}\n\turl = {origin}\n");

    [Fact]
    public void A_checkout_reads_its_origin_and_repository_name()
    {
        var folder = Checkout("prompt saver", "git@github.com:Team/prompt-saver.git");

        var info = GitRepoIdentity.Read(folder);

        Assert.Equal(new GitRepoInfo("github.com/Team/prompt-saver", "prompt-saver"), info);
    }

    [Fact]
    public void A_worktree_file_follows_the_pointer_to_the_main_config()
    {
        var main = Checkout("main", "https://github.com/Team/App.git");
        var worktreeGit = Path.Combine(main, ".git", "worktrees", "wt");
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");
        var worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + worktreeGit + "\n");

        Assert.Equal("github.com/Team/App", GitRepoIdentity.Read(worktree)?.Id);
    }

    [Fact]
    public void A_submodule_file_with_a_relative_pointer_reads_its_own_config()
    {
        var superproject = Checkout("super", "https://github.com/Team/Super.git");
        var moduleGit = Path.Combine(superproject, ".git", "modules", "lib");
        Directory.CreateDirectory(moduleGit);
        File.WriteAllText(Path.Combine(moduleGit, "config"), Config("https://github.com/Team/Lib.git"));
        var submodule = Path.Combine(superproject, "lib");
        Directory.CreateDirectory(submodule);
        File.WriteAllText(Path.Combine(submodule, ".git"), "gitdir: ../.git/modules/lib\n");

        Assert.Equal("github.com/Team/Lib", GitRepoIdentity.Read(submodule)?.Id);
    }

    [Fact]
    public void A_missing_folder_a_plain_folder_and_a_checkout_without_origin_have_no_identity()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        Assert.Null(GitRepoIdentity.Read(Path.Combine(_root, "gone")));
        Assert.Null(GitRepoIdentity.Read(plain));
        Assert.Null(GitRepoIdentity.Read(Checkout("local", origin: null)));
        Assert.Null(GitRepoIdentity.Read(Checkout("other", "https://x.test/a/b", "[remote \"upstream\"]")));
        Assert.Null(GitRepoIdentity.Read(""));
        Assert.Null(GitRepoIdentity.Read("relative"));
    }

    private static StatsRecord Record(string project, int dayOffset = 0, long tokens = 100) =>
        new("claude", Day.AddDays(dayOffset), "claude-sonnet-4-5", project, tokens, 0, 0, 0, 0);

    private static ProjectAliases Build(IReadOnlyList<StatsRecord> records) =>
        ProjectAliasResolver.Build(records, [], Directory.Exists);

    [Fact]
    public void Two_checkouts_with_the_same_origin_are_one_project()
    {
        var first = Checkout("a-prompt saver", "https://github.com/Team/prompt-saver.git");
        var second = Checkout("b-prompt saver", "git@github.com:team/Prompt-Saver");
        var records = new[] { Record(first), Record(second, dayOffset: 3) };

        var aliases = Build(records);

        Assert.Equal(aliases.Resolve(first), aliases.Resolve(second));
        Assert.Single(aliases.Apply(records).Select(record => record.Project).Distinct(StringComparer.OrdinalIgnoreCase));
        var winner = aliases.Resolve(second);
        Assert.Single(aliases.FormerPaths(winner));
    }

    [Fact]
    public void The_folder_named_like_the_repository_wins_and_the_choice_does_not_depend_on_use()
    {
        var odd = Checkout("zzz-copy", "https://github.com/Team/prompt-saver.git");
        var named = Checkout("prompt-saver", "https://github.com/Team/prompt-saver.git");

        foreach (var oddIsNewer in new[] { false, true })
        {
            var aliases = Build([Record(odd, dayOffset: oddIsNewer ? 9 : 0), Record(named, dayOffset: oddIsNewer ? 0 : 9)]);
            Assert.Equal(named, aliases.Resolve(odd));
            Assert.Equal(named, aliases.Resolve(named));
        }
    }

    [Fact]
    public void A_checkout_that_is_gone_keeps_its_own_identity_apart_from_a_different_repository()
    {
        var existing = Checkout("one", "https://github.com/Team/One.git");
        var other = Checkout("two", "https://github.com/Team/Two.git");
        var gone = Path.Combine(_root, "gone", "uno");

        var aliases = Build([Record(existing), Record(other), Record(gone)]);

        Assert.Equal(existing, aliases.Resolve(existing));
        Assert.Equal(other, aliases.Resolve(other));
        Assert.Equal(gone, aliases.Resolve(gone));
    }

    [Fact]
    public void Checkouts_without_a_remote_stay_separate_projects()
    {
        var first = Checkout("x-tool", origin: null);
        var second = Checkout("y-tool", origin: null);

        var aliases = Build([Record(first), Record(second)]);

        Assert.Equal(first, aliases.Resolve(first));
        Assert.Equal(second, aliases.Resolve(second));
    }

    [Fact]
    public void A_worktree_counts_with_its_main_checkout()
    {
        var main = Checkout("app", "https://github.com/Team/App.git");
        var worktreeGit = Path.Combine(main, ".git", "worktrees", "wt");
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");
        var worktree = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + worktreeGit);

        var aliases = Build([Record(main), Record(worktree)]);

        Assert.Equal(main, aliases.Resolve(worktree));
    }

    [Fact]
    public void The_repository_is_looked_up_once_per_distinct_path()
    {
        var calls = new List<string>();
        var records = new[] { Record(@"C:\a\App"), Record(@"C:\a\App", dayOffset: 1), Record(@"D:\b\App") };

        ProjectAliasResolver.Build(records, [], _ => false, path =>
        {
            calls.Add(path);
            return new GitRepoInfo("github.com/Team/App", "App");
        });

        Assert.Equal(2, calls.Count);
    }
}
