using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AiUsage.Tests;

/// <summary>
/// Nothing tracked in the archive may carry a personal trace, and the archive's file list may
/// never grow without a conscious decision. Both tests read the real git-tracked state of the
/// repository, not a fixture, so they catch a leak the moment it is staged.
/// </summary>
public class ShipCleanTests
{
    // Matches an absolute local user path in any of the three OSes a contributor might use, e.g.
    // "C:\Users\<name>\...", "/home/<name>/...", "/Users/<name>/...". Case-insensitive: NTFS and the shell
    // both fold case, so "c:\users\..." is exactly as revealing as "C:\Users\...".
    private static readonly Regex UserPath = new(
        @"[A-Za-z]:\\Users\\[^\\""'\s]+|/home/[^/""'\s]+|/Users/[^/""'\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The negative lookahead excludes the installer script's own Pascal "external 'Name@dll.ext
    // stdcall'" declarations (e.g. "FindWindowA@user32.dll") - syntactically identical to an
    // email address up to the TLD, but a DLL name, not a mailbox.
    private static readonly Regex EmailAddress = new(
        @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.(?!(?:dll|exe|lib|sys|ocx)\b)[A-Za-z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Numbered references into a private task list ("Punkt 22", "Schritt 5", "item 4", "step 29")
    // and the section marks such a list numbers its entries with ("§613"). None of this means
    // anything to a reader of the code, so none of it belongs in a shipped comment.
    private static readonly Regex ProcessTrace = new(
        @"\b(?:Punkt|Schritt|item|steps?)[\s-]+\d+\b|§\s*\d",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Terms that only mean something on a maintainer's own machine (names, local folders, private
    // tool and document names) are not spelled out in the public repository. They live in an
    // untracked file next to this one, one regular expression per line; a line starting with
    // "!planted:" instead carries a sample the expressions must catch. Without the file (a fresh
    // clone) only the generic checks in this class run.
    private const string PrivateTermsFileName = "ShipClean.private.txt";

    private static readonly (Regex? Terms, List<string> Planted) PrivateTerms = LoadPrivateTerms();

    private static (Regex? Terms, List<string> Planted) LoadPrivateTerms()
    {
        var path = Path.Combine(FindRepoRoot(), "tests", "AiUsage.Tests", PrivateTermsFileName);
        if (!File.Exists(path))
            return (null, []);

        var patterns = new List<string>();
        var planted = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("!planted:", StringComparison.Ordinal))
                planted.Add(line["!planted:".Length..].TrimStart());
            else
                patterns.Add("(?:" + line + ")");
        }

        return patterns.Count == 0
            ? (null, planted)
            : (new Regex(string.Join('|', patterns), RegexOptions.IgnoreCase | RegexOptions.Compiled), planted);
    }

    // Binary/text-unfriendly formats: a regex scan over their bytes would either throw on invalid
    // UTF-8 or, worse, "match" noise inside compressed data. Nothing sensitive can hide inside an
    // icon or a font in a way this test could meaningfully catch anyway.
    private static readonly string[] BinaryExtensions = [".ico", ".png", ".jpg", ".jpeg", ".gif"];

    // Two kinds of unavoidable self-reference, excluded by name rather than by loosening a pattern
    // (a genuine leak anywhere else must still fail the scan): PathSanitizerTests feeds a fake
    // "C:\Users\SomePerson\..." string in as INPUT to prove the sanitizer strips it, and this file
    // necessarily spells out every banned word and pattern it is checking for.
    private static readonly string[] ExemptFiles =
    [
        "tests/AiUsage.Tests/PathSanitizerTests.cs",
        "tests/AiUsage.Tests/ShipCleanTests.cs",
    ];

    // The archive's positive list. Prefixes end in "/" and match any file under that folder;
    // everything else must match a listed name or extension exactly, so a new top-level file is
    // always a conscious decision.
    private static readonly string[] AllowedPrefixes =
    [
        "src/", "tests/", "tools/", "installer/", ".github/ISSUE_TEMPLATE/", ".github/images/",
        ".github/workflows/",
    ];

    private static readonly string[] AllowedFiles =
    [
        "README.md", "CHANGELOG.md", "LICENSE", "SECURITY.md", "CONTRIBUTING.md", "CODE_OF_CONDUCT.md",
        ".gitignore", ".gitattributes", ".editorconfig",
        ".github/pull_request_template.md", ".github/dependabot.yml", "build.bat", "Directory.Build.props",
    ];

    // A comment sentence that wraps carries "///" at the start of its next line, which splits every
    // pattern above that spans a space. "(see item" and "141's account label)" sat on two lines and
    // slipped through as two harmless halves for exactly that reason. Folding the continuation
    // markers away before the scan makes a wrapped comment read as the one sentence it is.
    private static readonly Regex CommentContinuation =
        new(@"\r?\n[ \t]*(?:///?|\*)[ \t]*", RegexOptions.Compiled);

    private static string UnwrapComments(string text) => CommentContinuation.Replace(text, " ");

    [Fact]
    public void Tracked_files_contain_no_local_user_paths_or_email_addresses()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var relativePath in GitLsFiles(repoRoot))
        {
            if (BinaryExtensions.Contains(Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase))
                continue;
            if (ExemptFiles.Contains(relativePath, StringComparer.Ordinal))
                continue;

            var fullPath = Path.Combine(repoRoot, relativePath);
            var text = UnwrapComments(File.ReadAllText(fullPath));

            if (UserPath.IsMatch(text))
                offenders.Add($"{relativePath}: contains a local user path");
            if (EmailAddress.IsMatch(text))
                offenders.Add($"{relativePath}: contains an email address");
            if (ProcessTrace.IsMatch(text))
                offenders.Add($"{relativePath}: contains a plan-item reference");
            if (PrivateTerms.Terms?.Match(text) is { Success: true } hit)
                offenders.Add($"{relativePath}: contains a private term (\"{hit.Value}\")");
        }

        Assert.True(offenders.Count == 0, "Ship-clean violation(s):\n" + string.Join('\n', offenders));
    }

    // A shipped text may only point at markdown files the archive really contains; any other
    // "Name.md" is a dead pointer for anyone reading the code.
    private static readonly Regex MarkdownReference = new(@"\b[A-Za-z][A-Za-z-]*\.md\b", RegexOptions.Compiled);

    private static readonly string[] ShippedMarkdown = ["README.md", "CHANGELOG.md", "SECURITY.md", "CONTRIBUTING.md", "CODE_OF_CONDUCT.md"];

    // The project writes plain hyphens. The one exception is the empty-value placeholder in the tray
    // tooltip, which is the en dash on its own inside quotes (TrayTooltipBuilder.cs) or, once it is
    // assembled into a full tooltip line in a test assertion, the same dash flanked by a space and
    // the "middle dot" segment separator - both shapes are stripped out before this runs, and
    // widening scope to include the test project is what makes the second shape reachable at all.
    private static readonly Regex LongDash = new("[–—]", RegexOptions.Compiled);

    private static readonly Regex PlaceholderDash = new(
        "\"–\"|(?<=\\s)–(?=\\s·)", RegexOptions.Compiled);

    // Comments are English throughout, so a German function word inside one is a leftover. The list
    // is short and unambiguous: none of these is also an English word.
    private static readonly Regex GermanComment = new(
        @"//.*\b(?:nie|nicht|eine|der|die|das|und|wird|bleibt|Anbieter|Kachel|Fenster)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A `//`/`///` comment whose citation was deleted by hand can leave the sentence's own closing
    // punctuation stranded at the very start of the next line ("///. Never throws...") instead of
    // attached to the line before it, so the comment now opens mid-sentence. Anchored per line
    // (RegexOptions.Multiline) so it points at the exact offending line, not just the whole file.
    private static readonly Regex OrphanedCommentPunctuation = new(
        @"^[ \t]*///?[ \t]*[.:,;]", RegexOptions.Multiline | RegexOptions.Compiled);

    // Same defect shape, XAML flavor: "<!--: text" instead of "<!-- Text" once the parenthetical
    // citation that used to sit before the colon is gone.
    private static readonly Regex OrphanedXamlComment = new("<!--:", RegexOptions.Compiled);

    /// <summary>
    /// Scoped to what actually ships as readable text: the app's own source, the test project's own
    /// source, the installer script, the build script, and the two markdown files in the archive.
    /// PathSanitizerTests.cs and this file legitimately spell out the very patterns these guards
    /// look for, so both are exempted by name via <see cref="ExemptFiles"/> instead of weakening the
    /// checks for everyone else.
    /// </summary>
    [Fact]
    public void Shipped_text_cites_no_internal_document_and_stays_in_one_language()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var relativePath in GitLsFiles(repoRoot))
        {
            if (ExemptFiles.Contains(relativePath, StringComparer.Ordinal))
                continue;

            var isSource = (relativePath.StartsWith("src/", StringComparison.Ordinal)
                    || relativePath.StartsWith("tests/", StringComparison.Ordinal))
                && relativePath.EndsWith(".cs", StringComparison.Ordinal);
            var isShippedText = isSource
                || relativePath is "README.md" or "CHANGELOG.md" or "SECURITY.md" or "CONTRIBUTING.md" or "build.bat"
                || relativePath.StartsWith(".github/", StringComparison.Ordinal)
                || relativePath.StartsWith("installer/", StringComparison.Ordinal)
                || (relativePath.StartsWith("src/", StringComparison.Ordinal)
                    && (relativePath.EndsWith(".xaml", StringComparison.Ordinal)
                        || relativePath.EndsWith(".resx", StringComparison.Ordinal)));
            if (!isShippedText)
                continue;

            var text = File.ReadAllText(Path.Combine(repoRoot, relativePath));

            foreach (Match reference in MarkdownReference.Matches(text))
            {
                if (!ShippedMarkdown.Contains(reference.Value, StringComparer.Ordinal))
                    offenders.Add($"{relativePath}: cites {reference.Value}, which the archive does not contain");
            }
            if (LongDash.IsMatch(PlaceholderDash.Replace(text, "")))
                offenders.Add($"{relativePath}: contains an em or en dash");
            if (isSource && GermanComment.IsMatch(text))
                offenders.Add($"{relativePath}: has a German comment");
            if (OrphanedCommentPunctuation.IsMatch(text))
                offenders.Add($"{relativePath}: a comment opens mid-sentence with leftover punctuation");
            if (OrphanedXamlComment.IsMatch(text))
                offenders.Add($"{relativePath}: an XAML comment opens mid-sentence with leftover punctuation");
        }

        Assert.True(offenders.Count == 0, "Ship-clean violation(s):\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Private_terms_catch_every_planted_sample()
    {
        if (PrivateTerms.Terms is null)
            return; // A fresh clone has no private list; the generic checks above still run.

        Assert.NotEmpty(PrivateTerms.Planted);
        var missed = PrivateTerms.Planted.Where(sample => !PrivateTerms.Terms.IsMatch(sample)).ToList();
        Assert.True(missed.Count == 0, "Private terms miss planted sample(s):\n" + string.Join('\n', missed));
    }

    [Theory]
    [InlineData("reads ~/.claude/projects/*/*.jsonl")]
    [InlineData("the Claude Code sign-in")]
    [InlineData("C--Projects-AI-Usage")]
    [InlineData("see README.md and SECURITY.md")]
    public void Private_terms_leave_product_words_alone(string text)
    {
        if (PrivateTerms.Terms is not null)
            Assert.DoesNotMatch(PrivateTerms.Terms, text);
    }

    [Fact]
    public void Markdown_reference_check_detects_a_planted_violation()
    {
        var references = MarkdownReference.Matches("// follows NOTES.md, see README.md").Select(m => m.Value);

        Assert.Equal(["NOTES.md"], references.Where(r => !ShippedMarkdown.Contains(r)));
    }

    // Image files carry no text chunks: generators and editors write provenance records (tool name,
    // contact address) and editing history into PNG ancillary chunks, which the text scans above
    // cannot see. Checked for every tracked PNG and for PNGs embedded in a tracked SVG.
    private static readonly string[] AllowedPngChunks = ["IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "pHYs", "bKGD", "sBIT"];

    private static readonly Regex EmbeddedPng = new(@"data:image/png;base64,([A-Za-z0-9+/=]+)", RegexOptions.Compiled);

    internal static List<string> ForeignPngChunks(byte[] png)
    {
        var foreign = new List<string>();
        var offset = 8;
        while (offset + 8 <= png.Length)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (!AllowedPngChunks.Contains(type, StringComparer.Ordinal))
                foreign.Add(type);
            if (length < 0)
                break;
            offset += 12 + length;
        }
        return foreign;
    }

    [Fact]
    public void Tracked_images_carry_no_metadata_chunks()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var relativePath in GitLsFiles(repoRoot))
        {
            var fullPath = Path.Combine(repoRoot, relativePath);
            var pngs = new List<byte[]>();
            if (relativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                pngs.Add(File.ReadAllBytes(fullPath));
            else if (relativePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                pngs.AddRange(EmbeddedPng.Matches(File.ReadAllText(fullPath)).Select(m => Convert.FromBase64String(m.Groups[1].Value)));

            foreach (var png in pngs)
            {
                var foreign = ForeignPngChunks(png);
                if (foreign.Count > 0)
                    offenders.Add($"{relativePath}: metadata chunk(s) {string.Join(", ", foreign.Distinct())}");
            }
        }

        Assert.True(offenders.Count == 0, "Ship-clean violation(s):\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Image_metadata_check_detects_a_planted_provenance_chunk()
    {
        byte[] Chunk(string type, int length) =>
            [.. new byte[] { 0, 0, (byte)(length >> 8), (byte)length }, .. System.Text.Encoding.ASCII.GetBytes(type), .. new byte[length + 4]];
        byte[] planted = [137, 80, 78, 71, 13, 10, 26, 10, .. Chunk("IHDR", 13), .. Chunk("caBX", 20), .. Chunk("IEND", 0)];

        Assert.Equal(["caBX"], ForeignPngChunks(planted));
    }

    [Fact]
    public void Shipped_text_orphaned_comment_punctuation_check_detects_a_planted_violation()
    {
        const string planted = "/// Pure line parser, testable against inline fixtures alone\n" +
                                "///. Never throws: any shape it does not recognise is simply not a match.\n";

        Assert.Matches(OrphanedCommentPunctuation, planted);
    }

    [Fact]
    public void Shipped_text_orphaned_xaml_comment_check_detects_a_planted_violation()
    {
        const string planted = "<StackPanel>\n<!--: the real product mark. -->\n</StackPanel>";

        Assert.Matches(OrphanedXamlComment, planted);
    }

    [Fact]
    public void Archive_file_list_matches_the_positive_list()
    {
        var repoRoot = FindRepoRoot();
        var offenders = GitLsFiles(repoRoot)
            .Where(path => !IsAllowed(path))
            .ToList();

        Assert.True(offenders.Count == 0,
            "File(s) tracked but not on the archive's positive list:\n" + string.Join('\n', offenders));
    }

    private static bool IsAllowed(string relativePath)
    {
        if (relativePath == "AiUsage.slnx" || relativePath.EndsWith(".slnx", StringComparison.Ordinal))
            return true;
        if (AllowedFiles.Contains(relativePath, StringComparer.Ordinal))
            return true;
        return AllowedPrefixes.Any(prefix => relativePath.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static List<string> GitLsFiles(string repoRoot)
    {
        var startInfo = new ProcessStartInfo("git", "ls-files")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git ls-files failed to start.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    // Type scale, spacing and radii live in Themes/Tokens.xaml as named resources, not as bare
    // numbers scattered across every window. All three scans below are scoped to the app's own XAML
    // (never the test project, which does not ship) and read attribute syntax only
    // ("FontSize=\"12\""); a Setter's Property="FontSize" Value="12" is a different, unrelated XAML
    // shape and stays out of scope.
    private static readonly Regex FontSizeLiteral = new(@"FontSize=""[0-9]+""", RegexOptions.Compiled);

    // A Thickness value is 1-4 comma-separated numbers; WPF cannot resolve a StaticResource inside
    // one, so a mixed Margin/Padding (e.g. "16,0,0,0") has to stay a literal string of already-snapped
    // numbers rather than a resource reference. A negative component (TitleBar's badge offset,
    // Controls.xaml's slider-thumb halo) is an optical effect, not spacing, and is exempt the same way
    // the design decided for those two spots.
    private static readonly Regex MarginPaddingLiteral = new(
        @"(?:Margin|Padding)=""(-?[0-9]+(?:,-?[0-9]+){0,3})""", RegexOptions.Compiled);

    private static readonly int[] AllowedSpacing = [0, 4, 8, 12, 16, 24];

    // The two gaps inside a Full tile that are tighter than the spacing scale's smallest step: the
    // bar-row gap and the gap above the tile body. Exact literals, so no other 2 or 6 slips in.
    private static readonly string[] TightTileSpacingLiterals = ["0,0,0,6", "0,2,0,0"];

    // Same Thickness limitation applies to a 4-value CornerRadius: the window chrome's top-only
    // rounding ("12,12,0,0" in every *Window.xaml, on the DockPanel.Dock="Top" title-bar border)
    // cannot become a single StaticResource without inventing a new token for one specific shape, so
    // its two numbers (12 = Radius.Window, 0 = no rounding) are allowed to stay literal as long as
    // both are themselves valid radius values. The bar-radius exceptions (half the bar's own height,
    // documented next to each with a comment) are the only single-value literals allowed to remain.
    private static readonly Regex CornerRadiusLiteral = new(@"CornerRadius=""([0-9]+(?:,[0-9]+){0,3})""", RegexOptions.Compiled);

    private static readonly int[] AllowedCornerRadiusNumbers = [0, 2, 4, 6, 8, 12];

    // The two documented bar radii: a progress/slider bar's own corner is geometry (half the bar's
    // height), not a design token - Controls.xaml's Slider track/fill, ProviderTile's mini bar and
    // UsageBar's main bar all carry a comment saying so immediately above.
    private static readonly (string File, int Value)[] BarRadiusExceptionFiles =
    [
        ("Themes/Controls.xaml", 2),
        ("Views/Controls/ProviderTile.xaml", 2),
        ("Views/Controls/UsageBar.xaml", 4),
    ];

    [Fact]
    public void Xaml_uses_type_scale_tokens_instead_of_hardcoded_font_sizes()
    {
        var offenders = ScanAppXaml(text => FontSizeLiteral.Matches(text)
            .Select(m => $"literal {m.Value}"));

        Assert.True(offenders.Count == 0, "Ship-clean violation(s):\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Xaml_margin_and_padding_literals_only_use_the_spacing_scale()
    {
        var offenders = ScanAppXaml(text => MarginPaddingLiteral.Matches(text)
            .Where(m => !m.Groups[1].Value.Contains('-') && !TightTileSpacingLiterals.Contains(m.Groups[1].Value))
            .SelectMany(m => m.Groups[1].Value.Split(','), (m, n) => (m, n))
            .Where(pair => !AllowedSpacing.Contains(int.Parse(pair.n)))
            .Select(pair => $"literal {pair.m.Value} uses {pair.n}, not one of 0/4/8/12/16/24"));

        Assert.True(offenders.Count == 0, "Ship-clean violation(s):\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Xaml_corner_radius_literals_are_only_the_documented_geometry_exceptions()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var relativePath in AppXamlFiles(repoRoot))
        {
            if (relativePath.EndsWith("Themes/Tokens.xaml", StringComparison.Ordinal))
                continue;

            var text = File.ReadAllText(Path.Combine(repoRoot, relativePath));
            foreach (Match m in CornerRadiusLiteral.Matches(text))
            {
                var numbers = m.Groups[1].Value.Split(',').Select(int.Parse).ToArray();
                if (numbers.Any(n => !AllowedCornerRadiusNumbers.Contains(n)))
                {
                    offenders.Add($"{relativePath}: literal {m.Value} uses a value outside 0/4/6/8/12");
                    continue;
                }
                if (numbers.Length > 1)
                    continue; // window-chrome top rounding, e.g. "12,12,0,0" - see field comment above.

                var isBarException = BarRadiusExceptionFiles
                    .Any(e => relativePath.EndsWith(e.File, StringComparison.Ordinal) && numbers[0] == e.Value);
                if (!isBarException)
                    offenders.Add($"{relativePath}: literal {m.Value} - not a StaticResource and not a documented bar radius");
            }
        }

        Assert.True(offenders.Count == 0, "Ship-clean violation(s):\n" + string.Join('\n', offenders));
    }

    // A Button element can be many lines long (attributes on continuation lines are common in this
    // codebase, e.g. a Style="..." set three lines below the opening <Button) - [^>] matches across
    // line breaks in .NET regex (it excludes only the literal '>' character, not '\n'), so this finds
    // the whole element regardless of how its attributes are wrapped, unlike a line-based grep.
    private static readonly Regex ButtonElement = new(@"<Button\b[^>]*>", RegexOptions.Compiled);

    [Fact]
    public void Xaml_buttons_all_carry_an_explicit_style()
    {
        var offenders = ScanAppXaml(text => ButtonElement.Matches(text)
            .Where(m => !m.Value.Contains("Style=", StringComparison.Ordinal))
            .Select(m => $"<Button with no Style attribute: {m.Value.Trim()}"));

        Assert.True(offenders.Count == 0, "Ship-clean violation(s):\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Xaml_buttons_all_carry_an_explicit_style_check_detects_a_planted_violation()
    {
        const string planted = "<Button Click=\"Foo_Click\"\n        Width=\"10\"/>";

        var matches = ButtonElement.Matches(planted)
            .Where(m => !m.Value.Contains("Style=", StringComparison.Ordinal))
            .ToList();

        Assert.Single(matches);
    }

    private static List<string> ScanAppXaml(Func<string, IEnumerable<string>> findViolations)
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var relativePath in AppXamlFiles(repoRoot))
        {
            var text = File.ReadAllText(Path.Combine(repoRoot, relativePath));
            offenders.AddRange(findViolations(text).Select(v => $"{relativePath}: {v}"));
        }

        return offenders;
    }

    private static IEnumerable<string> AppXamlFiles(string repoRoot) =>
        GitLsFiles(repoRoot).Where(p => p.StartsWith("src/AiUsage/", StringComparison.Ordinal)
            && p.EndsWith(".xaml", StringComparison.Ordinal));

    // A worktree's own root has a ".git" FILE (pointing at the real repo's .git/worktrees/<name>),
    // not a ".git" directory - checking only Directory.Exists (as elsewhere in this test project)
    // walks straight past a worktree root and finds the main checkout's .git directory instead,
    // silently scanning the wrong copy of the source. Checking either keeps this correct in both.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find the repository root (.git) above " + AppContext.BaseDirectory);
    }
}
