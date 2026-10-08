using AiUsage.Services;
using AiUsage.Storage;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class SharedLogServiceTests
{
    [Fact]
    public async Task Two_threads_writing_through_Shared_lose_no_line()
    {
        var logsDirectory = AppPaths.LogsDirectory;
        var marker = "shared-" + Guid.NewGuid().ToString("N");

        void Write(int thread)
        {
            for (var i = 0; i < 500; i++)
                LogService.Shared.LogInfo($"{marker} thread {thread} line {i}");
        }

        await Task.WhenAll(Task.Run(() => Write(1)), Task.Run(() => Write(2)));

        var count = Directory.EnumerateFiles(logsDirectory, "app*.log")
            .SelectMany(File.ReadLines)
            .Count(line => line.Contains(marker));
        Assert.Equal(1000, count);
    }

    [Fact]
    public void Shared_follows_the_data_folder_of_each_write()
    {
        Assert.Equal(Path.Combine(AppPaths.LogsDirectory, "app.log"), LogService.Shared.CurrentFile);
    }
}
