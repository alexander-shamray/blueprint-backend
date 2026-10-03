using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Notifications.TestSupport;

/// <summary>Every line the host logs, with scopes, state values and exception text, so a search misses none.</summary>
/// <remarks>§13.4 keeps a mailbox out of every log attribute and every exception's text.</remarks>
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
        // Common.Web sets IncludeScopes, so a scope's state reaches an exporter and is searched too.
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            lines.Enqueue(state.ToString() ?? string.Empty);

            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (KeyValuePair<string, object?> value in values)
                    lines.Enqueue($"{value.Key}={value.Value}");
            }

            return NoScope.Instance;
        }

        // No filtering of its own: what the host's rules let through is what an exporter receives.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lines.Enqueue(formatter(state, exception));

            // The structured half, which an exporter writes as attributes rather than into the message.
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (KeyValuePair<string, object?> value in values)
                    lines.Enqueue($"{value.Key}={value.Value}");
            }

            if (exception is not null)
                lines.Enqueue(exception.ToString());
        }
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}
