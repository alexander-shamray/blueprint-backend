using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Common.Web.Tests;

/// <summary>Captures pushed scopes, for §10.4's claim that every line below the middleware carries the ID.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<object> _scopes = new();

    internal IReadOnlyCollection<object> Scopes => _scopes;

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(_scopes);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(ConcurrentQueue<object> scopes) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            scopes.Enqueue(state);
            return NoOpScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class NoOpScope : IDisposable
    {
        internal static readonly NoOpScope Instance = new();

        public void Dispose()
        {
        }
    }
}
