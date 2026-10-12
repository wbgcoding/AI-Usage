using System.Text.RegularExpressions;
using AiUsage.Services;

namespace AiUsage.Tests;

public class Win32DeclarationTests
{
    [Fact]
    public void Each_user32_entry_point_is_declared_once_in_the_services()
    {
        var folder = Path.Combine(RepoRoot.Find(), "src", "AiUsage", "Services");
        var declared = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("DllImport(\"user32.dll\"", StringComparison.Ordinal))
                    continue;

                // The declaration follows, possibly after a return attribute line.
                for (var j = i + 1; j < Math.Min(i + 4, lines.Length); j++)
                {
                    var match = Regex.Match(lines[j], @"static extern \S+ (\w+)\(");
                    if (!match.Success)
                        continue;
                    declared.Add(match.Groups[1].Value);
                    break;
                }
            }
        }

        Assert.NotEmpty(declared);
        Assert.Equal([], declared.GroupBy(name => name).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void The_shared_window_calls_live_in_the_one_win32_class()
    {
        var source = SourceScan.Read("src", "AiUsage", "Services", "Win32.cs");

        foreach (var name in new[]
                 {
                     "GetWindowLongPtr", "SetWindowLongPtr", "ShowWindow", "GetWindowRect", "SetWinEventHook",
                     "UnhookWinEvent", "GetClassName", "MonitorFromWindow", "GetMonitorInfo", "ClassNameOf",
                 })
        {
            Assert.Contains(name, source, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0x0004u, true)] // NOZORDER alone: a resize
    [InlineData(0x0004u | 0x0001u, false)] // NOZORDER + NOSIZE: a plain move
    [InlineData(0u, true)] // nothing set: the order may change
    [InlineData(0x0001u, true)] // NOSIZE only: the order may change
    public void The_stack_is_only_listed_for_a_change_that_can_move_the_window_in_it(uint flags, bool expected)
    {
        Assert.Equal(expected, DesktopLayer.NeedsSlotCheck(flags));
    }

    [Fact]
    public void The_full_screen_watcher_has_no_hook_on_window_movement_and_polls_while_on()
    {
        var source = SourceScan.Read("src", "AiUsage", "Services", "FullscreenWatcher.cs");

        Assert.DoesNotContain("LocationChange", source, StringComparison.Ordinal);
        Assert.DoesNotContain("0x800B", source, StringComparison.Ordinal);
        var enabled = SourceScan.Member(source, "public bool Enabled");
        Assert.Contains("_poll.Start()", enabled, StringComparison.Ordinal);
        Assert.Contains("_poll.Stop()", enabled, StringComparison.Ordinal);
    }

    [Fact]
    public void The_desktop_walk_stops_at_the_first_desktop_window()
    {
        var source = SourceScan.Read("src", "AiUsage", "Services", "DesktopLayer.cs");
        var slot = SourceScan.Member(source, "private Slot CurrentSlot()");

        Assert.Contains("return !(desktop && hwnd != _hwnd);", slot, StringComparison.Ordinal);
    }
}
