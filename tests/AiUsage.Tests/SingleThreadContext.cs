using System.Collections.Concurrent;

namespace AiUsage.Tests;

/// <summary>A one-thread message loop standing in for the UI thread: work posted to it and the
/// continuations of an await started on it all run on that one thread.</summary>
internal sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State, ManualResetEventSlim? Done)> _queue = [];
    private readonly Thread _thread;

    public SingleThreadContext()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "test-ui" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state, null));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == ThreadId)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        _queue.Add((d, state, done));
        done.Wait();
    }

    /// <summary>Runs <paramref name="work"/> on the loop thread and returns its result.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                source.SetResult(await work());
            }
            catch (Exception ex)
            {
                source.SetException(ex);
            }
        }, null);
        return source.Task;
    }

    private void Loop()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state, done) in _queue.GetConsumingEnumerable())
        {
            try
            {
                callback(state);
            }
            finally
            {
                done?.Set();
            }
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
