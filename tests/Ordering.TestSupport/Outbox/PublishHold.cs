using MassTransit;

namespace Ordering.TestSupport.Outbox;

/// <summary>Holds every publish until released, and records the most held at once.</summary>
/// <remarks>An observer on the bus, not a replaced <see cref="IPublishEndpoint"/>, so the host's own publish path
/// is the one measured.</remarks>
public sealed class PublishHold(int full) : IPublishObserver
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _held;
    private int _most;

    /// <summary>Completes once <c>full</c> publishes are held at once.</summary>
    public TaskCompletionSource Full { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Most
    {
        get
        {
            lock (_gate)
                return _most;
        }
    }

    public void Release() => _released.TrySetResult();

    public async Task PrePublish<T>(PublishContext<T> context)
        where T : class
    {
        lock (_gate)
        {
            _most = Math.Max(_most, ++_held);

            if (_held >= full)
                Full.TrySetResult();
        }

        try
        {
            await _released.Task;
        }
        finally
        {
            lock (_gate)
                _held--;
        }
    }

    public Task PostPublish<T>(PublishContext<T> context)
        where T : class =>
        Task.CompletedTask;

    public Task PublishFault<T>(PublishContext<T> context, Exception exception)
        where T : class =>
        Task.CompletedTask;
}
