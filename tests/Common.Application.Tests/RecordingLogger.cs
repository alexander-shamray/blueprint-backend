using Microsoft.Extensions.Logging;

namespace Common.Application.Tests;

/// <summary>One log line, flattened the way a provider would flatten it.</summary>
public sealed record LogLine(LogLevel Level, string Message, Exception? Exception);

/// <summary>What the pipeline wrote, scopes included, since §13.3 pushes <c>RequestType</c> as a scope.</summary>
public sealed class LogSink
{
    private readonly List<LogLine> _lines = [];
    private readonly List<object?> _scopes = [];

    public IReadOnlyList<LogLine> Lines => _lines;

    public IReadOnlyList<object?> Scopes => _scopes;

    public void Add(LogLine line) => _lines.Add(line);

    public void AddScope(object? state) => _scopes.Add(state);
}

/// <summary>An open-generic logger over <see cref="LogSink"/>, as §4.2 leaves no logger factory in reach.</summary>
public sealed class RecordingLogger<T>(LogSink sink) : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        sink.AddScope(state);
        return NoopScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        sink.Add(new LogLine(logLevel, formatter(state, exception), exception));

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
        }
    }
}
