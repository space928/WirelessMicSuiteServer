using System.Collections.Concurrent;

namespace WirelessMicSuiteServer;

public class LocalSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback fn, object? arg, ManualResetEventSlim? waitHandle)> workItems = new();
    private readonly CancellationToken cancellationToken;

    public LocalSynchronizationContext(CancellationToken cancellationToken)
    {
        this.cancellationToken = cancellationToken;
    }

    public void Run()
    {
        SpinWait wait = default;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!workItems.TryDequeue(out var work))
            {
                wait.SpinOnce();
                continue;
            }

            wait = default;

            work.fn(work.arg);
            work.waitHandle?.Set();
        }
    }

    public override SynchronizationContext CreateCopy()
    {
        return new LocalSynchronizationContext(cancellationToken);
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        workItems.Enqueue((d, state, null));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        using var wait = new ManualResetEventSlim(false);
        workItems.Enqueue((d, state, wait));
        wait.Wait();
    }
}
