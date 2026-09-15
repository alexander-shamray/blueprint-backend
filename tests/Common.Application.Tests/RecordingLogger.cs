using Microsoft.Extensions.Logging;

namespace Common.Application.Tests;

/// <summary>One log line, flattened the way a provider would flatten it.</summary>
public sealed record LogLine(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// What the pipeline wrote. Scopes are kept as well as lines, because §13.3's
/// behaviour pushes <c>RequestType</c> as a scope rather than a property —
/// a test that only read the lines would pass on a behaviour that had stopped
/// pushing one.
/// </summary>
public sealed class LogSink
{
    private readonly List<LogLine> _lines = [];
    private readonly List<object?> _scopes = [];

    public IReadOnlyList<LogLine> Lines => _lines;

    public IReadOnlyList<object?> Scopes => _scopes;

    public void Add(LogLine line) => _lines.Add(line);

    public void AddScope(object? state) => _scopes.Add(state);
}

/// <summary>
/// An <see cref="ILogger{T}"/> over <see cref="LogSink"/>, registered as an
/// open generic. Common.Application references the logging abstractions and
/// nothing else (§4.2), so there is no <c>ILoggerFactory</c> implementation in
/// reach, and writing this logger avoids adding a logging package to get one.
/// </summary>
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
