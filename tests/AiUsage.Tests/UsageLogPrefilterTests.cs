using System.Text;
using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>The byte-level prefilters may only ever drop a line the parser would have ignored anyway:
/// parsing everything and parsing only what passes must give the same events and the same state.</summary>
public class UsageLogPrefilterTests
{
    private static readonly string FixturesDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static bool Passes(Func<byte[], bool> filter, string line) => filter(Encoding.UTF8.GetBytes(line));

    private static IEnumerable<string> FixtureLines(string pattern) =>
        Directory.GetFiles(FixturesDirectory, pattern)
            .SelectMany(File.ReadAllLines)
            .Where(line => line.Length > 0);

    private static readonly string[] ClaudeSynthetic =
    [
        """{"type":"assistant","timestamp":"2026-01-01T00:00:00Z","requestId":"r1","message":{"id":"m1","model":"claude-x","usage":{"input_tokens":4,"output_tokens":1,"cache_creation_input_tokens":2,"cache_read_input_tokens":3}}}""",
        """{"type":"assistant","timestamp":"2026-01-01T00:00:01Z","message":{"model":"claude-x","usage": {"input_tokens": 7}},"effort":"high"}""",
        """{"type":"user","timestamp":"2026-01-01T00:00:02Z","message":{"role":"user","content":"hello"}}""",
        """{"type":"user","timestamp":"2026-01-01T00:00:03Z","message":{"role":"user","content":"what is my usage today"}}""",
        """{"type":"summary","summary":"x","cwd":"C:\\Projects\\Sample"}""",
        """{"message":{"usage":{"input_tokens":0}},"timestamp":"2026-01-01T00:00:04Z"}""",
        """[1,2,3]""",
        """not json at all""",
        """{"type":"assistant","timestamp":"2026-01-01T00:00:05Z","message":{"content":[{"type":"tool_use","name":"usage"}]}}""",
    ];

    private static readonly string[] CodexSynthetic =
    [
        """{"timestamp":"2026-01-01T00:00:00Z","type":"session_meta","payload":{"id":"s1","cwd":"C:\\Projects\\Sample"}}""",
        """{"timestamp":"2026-01-01T00:00:01Z","type":"turn_context","payload":{"model":"gpt-x","effort":"medium"}}""",
        """{"timestamp":"2026-01-01T00:00:02Z","type":"response_item","payload":{"type":"message","role":"user"}}""",
        """{"timestamp":"2026-01-01T00:00:03Z","type":"event_msg","payload":{"type":"agent_message","message":"hi"}}""",
        """{"timestamp":"2026-01-01T00:00:04Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":40,"cache_write_input_tokens":0,"output_tokens":10}}}}""",
        """{"timestamp":"2026-01-01T00:00:05Z","type":"event_msg","payload":{"type":"token_count","info":null}}""",
        """{"timestamp":"2026-01-01T00:00:06Z","type":"turn_context","payload":{"model":"gpt-y","reasoning_effort":"high"}}""",
        """{"timestamp":"2026-01-01T00:00:07Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":250,"cached_input_tokens":90,"cache_write_input_tokens":0,"output_tokens":30}}}}""",
        """{"timestamp":"2026-01-01T00:00:08Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":20,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":5}}}}""",
        """{"timestamp":"2026-01-01T00:00:09Z","type":"response_item","payload":{"type":"function_call_output","output":"nothing relevant"}}""",
    ];

    [Fact]
    public void Every_claude_line_the_parser_accepts_passes_the_prefilter()
    {
        var lines = FixtureLines("claude-*.jsonl").Concat(ClaudeSynthetic).ToList();
        var accepted = 0;

        foreach (var line in lines)
        {
            if (!ClaudeUsageLogParser.TryParse(line, out _))
                continue;
            accepted++;
            Assert.True(Passes(b => ClaudeUsageLogParser.MayContainUsage(b), line), line);
        }

        Assert.True(accepted >= 2);
    }

    [Fact]
    public void Claude_events_are_identical_with_and_without_the_prefilter()
    {
        var lines = FixtureLines("claude-*.jsonl").Concat(ClaudeSynthetic).ToList();

        var all = lines.Where(l => ClaudeUsageLogParser.TryParse(l, out _))
            .Select(l => { ClaudeUsageLogParser.TryParse(l, out var e); return e; }).ToList();
        var filtered = lines.Where(l => Passes(b => ClaudeUsageLogParser.MayContainUsage(b), l))
            .Where(l => ClaudeUsageLogParser.TryParse(l, out _))
            .Select(l => { ClaudeUsageLogParser.TryParse(l, out var e); return e; }).ToList();

        Assert.NotEmpty(all);
        Assert.Equal(all, filtered);
        Assert.True(lines.Count(l => !Passes(b => ClaudeUsageLogParser.MayContainUsage(b), l)) > 0, "the filter should drop something");
    }

    [Fact]
    public void Codex_events_and_state_are_identical_with_and_without_the_prefilter()
    {
        var lines = FixtureLines("codex-*.jsonl").Concat(CodexSynthetic).ToList();

        var full = Run(lines);
        var filtered = Run(lines.Where(l => Passes(b => CodexUsageLogParser.MayAffectState(b), l)));

        Assert.NotEmpty(full.Events);
        Assert.Equal(full.Events, filtered.Events);
        Assert.Equal(full.State, filtered.State);
        Assert.True(lines.Count(l => !Passes(b => CodexUsageLogParser.MayAffectState(b), l)) > 0, "the filter should drop something");
    }

    [Fact]
    public void Every_codex_line_that_changes_the_parser_passes_the_prefilter()
    {
        // Feed every line to its own fresh parser seeded the same way: a line that moves the state
        // or yields an event is one the filter must keep.
        foreach (var line in FixtureLines("codex-*.jsonl").Concat(CodexSynthetic))
        {
            var parser = new CodexUsageLogParser();
            var before = parser.State;
            var produced = parser.TryParseLine(line, out _);
            if (produced || parser.State != before)
                Assert.True(Passes(b => CodexUsageLogParser.MayAffectState(b), line), line);
        }
    }

    private static (List<CodexUsageLogParser.UsageEvent> Events, (string, long, long, long, long, string) State) Run(IEnumerable<string> lines)
    {
        var parser = new CodexUsageLogParser();
        var events = new List<CodexUsageLogParser.UsageEvent>();
        foreach (var line in lines)
            if (parser.TryParseLine(line, out var e))
                events.Add(e);
        return (events, parser.State);
    }
}
