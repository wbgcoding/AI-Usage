using AiUsage.Services;

namespace AiUsage.Tests;

public class LogServiceTests : IDisposable
{
    [Fact]
    public void Log_writes_a_line_containing_the_level_and_message()
    {
        var directory = TempDirectory();
        var log = new LogService(directory);

        log.LogInfo("hello world");

        var text = File.ReadAllText(log.CurrentFile);
        Assert.Contains("[INFO]", text);
        Assert.Contains("hello world", text);
    }

    [Fact]
    public void Log_writes_utf8_without_a_byte_order_mark()
    {
        var directory = TempDirectory();
        var log = new LogService(directory);

        log.LogInfo("no bom");

        var bytes = File.ReadAllBytes(log.CurrentFile);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void Log_rolls_to_a_new_file_once_the_size_cap_is_exceeded()
    {
        var directory = TempDirectory();
        // A cap smaller than a single entry forces every write past the first to roll.
        var log = new LogService(directory, maxFileSizeBytes: 10, maxFiles: 5);

        log.LogInfo("entry one");
        log.LogInfo("entry two");

        Assert.True(File.Exists(Path.Combine(directory, "app.1.log")));
        Assert.Contains("entry one", File.ReadAllText(Path.Combine(directory, "app.1.log")));
        Assert.Contains("entry two", File.ReadAllText(Path.Combine(directory, "app.log")));
    }

    [Fact]
    public void The_sixth_rolling_entry_deletes_the_oldest_file()
    {
        var directory = TempDirectory();
        var log = new LogService(directory, maxFileSizeBytes: 10, maxFiles: 5);

        for (var i = 1; i <= 5; i++)
            log.LogInfo($"entry {i}");

        // Five entries fully populate app.log + app.1.log..app.4.log (5 files, the cap).
        Assert.True(File.Exists(Path.Combine(directory, "app.4.log")));
        Assert.Contains("entry 1", File.ReadAllText(Path.Combine(directory, "app.4.log")));

        log.LogInfo("entry 6");

        // The sixth entry rolls once more: entry 1 (the oldest) is pushed off the end and deleted.
        Assert.Contains("entry 2", File.ReadAllText(Path.Combine(directory, "app.4.log")));
        Assert.DoesNotContain("entry 1", Directory.EnumerateFiles(directory).SelectMany(File.ReadAllLines));
    }

    [Fact]
    public void Log_never_throws_even_when_the_directory_cannot_be_created()
    {
        // A file standing in for the log directory makes Directory.CreateDirectory fail.
        var blocker = TestPaths.GetPath("ai-usage-log-blocker");
        File.WriteAllText(blocker, "not a directory");
        try
        {
            var log = new LogService(Path.Combine(blocker, "logs"));

            var exception = Record.Exception(() => log.LogInfo("should not throw"));

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    private readonly List<DisposableTestDirectory> _tempDirectories = [];

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-log");
        _tempDirectories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }

    [Fact]
    public void Log_masks_the_user_profile_path_on_disk()
    {
        var directory = TempDirectory();
        var log = new LogService(directory);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        log.LogInfo($"could not open {Path.Combine(profile, "AppData", "x.json")}");

        var text = File.ReadAllText(log.CurrentFile);
        Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<user>", text);
    }
}
