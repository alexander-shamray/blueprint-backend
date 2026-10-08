using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace Common.TestSupport;

/// <summary>A fixture's calls to the daemon, made again after a refusal, so one busy daemon fails no suite.</summary>
/// <remarks>
/// The last attempt's exception propagates, so a machine with no daemon still fails rather than skips
/// (docs/testing.md, Docker is not optional).
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
}
