using Microsoft.Extensions.Logging;

namespace Common.Application;

/// <summary>Registered first, so outermost (§6.3): its span covers every behaviour and the handler.</summary>
/// <remarks>A returned failure is <c>ok</c>, because a refused command is not a broken system (§13.3).</remarks>
public sealed class LoggingBehavior<TRequest, TResult>(
    ILogger<LoggingBehavior<TRequest, TResult>> logger,
    RequestMetrics metrics,
    TimeProvider clock)
    : IPipelineBehavior<TRequest, TResult>
{
    private static readonly Action<ILogger, string, double, Exception?> Completed =
        LoggerMessage.Define<string, double>(
            LogLevel.Information,
            new EventId(1, nameof(Completed)),
            "{RequestType} completed in {ElapsedMs} ms");

    private static readonly Action<ILogger, string, Exception?> Threw =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2, nameof(Threw)),
            "{RequestType} threw");

    public async Task<TResult> HandleAsync(TRequest request, NextDelegate<TResult> next, CancellationToken ct)
    {
        string name = typeof(TRequest).Name;
        long start = clock.GetTimestamp();

        // A scope, not a log property, so EF Core's and MassTransit's logging inside the handler inherit it.
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["RequestType"] = name
        });

        try
        {
            TResult result = await next();

            // Read once, so the log line and the histogram carry the same number.
            TimeSpan elapsed = clock.GetElapsedTime(start);

            Completed(logger, name, elapsed.TotalMilliseconds, null);
            metrics.Recorded(name, "ok", elapsed);

            return result;
        }
        catch (Exception ex)
        {
            Threw(logger, name, ex);
            metrics.Recorded(name, "error", clock.GetElapsedTime(start));
            throw;
        }
    }
}
