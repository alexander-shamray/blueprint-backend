using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Shipping.TestSupport;

/// <summary>
/// Every line the host logs, exported whole: the message, each value the state
/// carries, and the exception's text, because spec section 11 keeps an address
/// out of all three and a search over messages alone would miss the rest.
/// </summary>
/// <remarks>
/// A <see cref="ConcurrentQueue{T}"/>, because entries arrive from the bus's
/// consumer threads and both workers at once; <see cref="Everything"/> copies.
/// </remarks>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>Every line captured so far, copied at the read.</summary>
    public IReadOnlyCollection<string> Everything => _lines.ToArray();

    /// <summary>Drops what earlier passes logged; the fixture's reset calls it.</summary>
    public void Clear() => _lines.Clear();

    public ILogger CreateLogger(string categoryName) => new Logger(_lines);

    public void Dispose()
    {
    }

    private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        // No filtering of its own: what the host's rules let through is what
        // a deployment's exporter receives, and that is what is searched.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lines.Enqueue(formatter(state, exception));

            // The structured half: LoggerMessage.Define hands the state over as
            // the template's name-value pairs, which an exporter writes as
            // attributes rather than into the message (spec, section 11).
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (KeyValuePair<string, object?> value in values)
                    lines.Enqueue($"{value.Key}={value.Value}");
            }

            if (exception is not null)
                lines.Enqueue(exception.ToString());
        }
    }
}
