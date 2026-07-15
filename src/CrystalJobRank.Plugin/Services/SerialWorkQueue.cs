using System.Threading.Channels;

namespace CrystalJobRank.Plugin.Services;

/// <summary>
/// Processes infrequent persistence work in admission order without blocking
/// the game framework thread. Disposal drains admitted work before returning.
/// </summary>
internal sealed class SerialWorkQueue<T> : IDisposable where T : notnull
{
    private readonly object lifecycleGate = new();
    private readonly Channel<WorkItem> channel;
    private readonly Action<T> process;
    private readonly Action<T, Exception> onError;
    private readonly Task worker;
    private bool accepting = true;

    public SerialWorkQueue(Action<T> process, Action<T, Exception> onError)
    {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        this.onError = onError ?? throw new ArgumentNullException(nameof(onError));
        channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        worker = Task.Run(ProcessAsync);
    }

    public bool TryEnqueue(T value)
    {
        lock (lifecycleGate)
        {
            return accepting && channel.Writer.TryWrite(WorkItem.ForValue(value));
        }
    }

    /// <summary>
    /// Waits until every item admitted before this call has completed. This is
    /// used before synchronous reset commands to preserve framework event order.
    /// </summary>
    public void Drain()
    {
        Task completion;
        lock (lifecycleGate)
        {
            if (!accepting)
            {
                completion = worker;
            }
            else
            {
                var drained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!channel.Writer.TryWrite(WorkItem.ForBarrier(drained)))
                {
                    throw new InvalidOperationException("The persistence queue stopped before it could be drained.");
                }
                completion = drained.Task;
            }
        }

        completion.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (accepting)
            {
                accepting = false;
                channel.Writer.TryComplete();
            }
        }

        worker.GetAwaiter().GetResult();
    }

    private async Task ProcessAsync()
    {
        await foreach (var item in channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item.Barrier is not null)
            {
                item.Barrier.TrySetResult(true);
                continue;
            }

            try
            {
                process(item.Value!);
            }
            catch (Exception exception)
            {
                try
                {
                    onError(item.Value!, exception);
                }
                catch
                {
                    // Diagnostics must never terminate the persistence worker.
                }
            }
        }
    }

    private readonly record struct WorkItem(T? Value, TaskCompletionSource<bool>? Barrier)
    {
        public static WorkItem ForValue(T value) => new(value, null);
        public static WorkItem ForBarrier(TaskCompletionSource<bool> barrier) => new(default, barrier);
    }
}
