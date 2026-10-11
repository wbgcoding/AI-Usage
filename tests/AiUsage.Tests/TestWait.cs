namespace AiUsage.Tests;

/// <summary>Waits for something a background task does instead of sleeping a fixed time: a fixed
/// sleep is too short on a loaded machine and too long everywhere else.</summary>
internal static class TestWait
{
    /// <summary>Polls <paramref name="condition"/> until it holds; fails the test after
    /// <paramref name="timeout"/> (10 s by default).</summary>
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The awaited condition did not come true in time.");
            await Task.Delay(5);
        }
    }
}
