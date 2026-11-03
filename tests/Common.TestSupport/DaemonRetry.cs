using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace Common.TestSupport;

/// <summary>A fixture's calls to the daemon, made again after a refusal, so one busy daemon fails no suite.</summary>
/// <remarks>
/// The last attempt's exception propagates, so a machine with no daemon still fails rather than skips
/// (§12.4).
/// </remarks>
public static class DaemonRetry
{
    public const int Attempts = 3;

    public static readonly TimeSpan Backoff = TimeSpan.FromSeconds(2);

    public static Task StartAsync(IContainer container, CancellationToken ct = default) =>
        RetryAsync(container.StartAsync, Backoff, ct);

    public static Task CreateAsync(IFutureDockerImage image, CancellationToken ct = default) =>
        RetryAsync(image.CreateAsync, Backoff, ct);

    /// <summary>Waits <paramref name="backoff"/> times the attempt number between attempts.</summary>
    public static async Task RetryAsync(Func<CancellationToken, Task> call, TimeSpan backoff, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await call(ct);
                return;
            }
            catch (Exception) when (attempt < Attempts)
            {
                await Task.Delay(backoff * attempt, ct);
            }
        }
    }

    /// <summary><see cref="RetryAsync"/>, each attempt cancelled and abandoned at <paramref name="budget"/>.</summary>
    /// <remarks>
    /// For a call that waits as long as its token allows, as a MassTransit start does under a caller's token; the
    /// switch bounds its topology deploy the same way (ADR-145).
    /// </remarks>
    public static Task RetryBoundedAsync(
        Func<CancellationToken, Task> call,
        TimeSpan budget,
        TimeSpan backoff,
        CancellationToken ct) =>
        RetryAsync(
            async outer =>
            {
                using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(outer);
                attempt.CancelAfter(budget);

                try
                {
                    // Awaited through the token too, so a call that ignores it cannot hold the attempt open.
                    await call(attempt.Token).WaitAsync(attempt.Token);
                }
                catch (OperationCanceledException e)
                    when (attempt.IsCancellationRequested && !outer.IsCancellationRequested)
                {
                    throw new TimeoutException($"The call did not finish within {budget}.", e);
                }
            },
            backoff,
            ct);
}
