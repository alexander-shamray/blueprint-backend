using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Common.Web;

/// <summary>§13.4's never-log rule for log scopes, which <see cref="SensitiveDataRedactor"/> cannot reach.</summary>
/// <remarks>Redacts on enumeration, not on <see cref="Push"/>, so the caller's scope object is untouched.</remarks>
public sealed class RedactingScopeProvider(IExternalScopeProvider inner, bool ownsInner = false)
    : IExternalScopeProvider, IDisposable, IAsyncDisposable
{
    /// <summary>Registers this wrapper around whatever <see cref="IExternalScopeProvider"/> came before it.</summary>
    /// <remarks>Wraps rather than defers, because §13.4 is a guarantee and not a default.</remarks>
    public static IServiceCollection WrapScopesForRedaction(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        ServiceDescriptor? existing = services.LastOrDefault(
            d => d.ServiceType == typeof(IExternalScopeProvider) && !d.IsKeyedService);

        if (existing is not null)
            services.Remove(existing);

        // Owns the inner provider when the container would have created, and so disposed, it.
        services.AddSingleton<IExternalScopeProvider>(
            sp => new RedactingScopeProvider(
                Inner(sp, existing),
                ownsInner: existing is not null && existing.ImplementationInstance is null));

        // A later registration would win, so the guard checks the resolved provider once the host starts.
        services.AddHostedService<ScopeRedactionGuard>();

        return services;
    }

    private static IExternalScopeProvider Inner(IServiceProvider sp, ServiceDescriptor? existing)
    {
        if (existing is null)
            return new LoggerExternalScopeProvider();

        if (existing.ImplementationInstance is IExternalScopeProvider instance)
            return instance;

        if (existing.ImplementationFactory is not null)
            return (IExternalScopeProvider)existing.ImplementationFactory(sp);

        return (IExternalScopeProvider)ActivatorUtilities.CreateInstance(
            sp,
            existing.ImplementationType!);
    }

    private readonly IExternalScopeProvider _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public void ForEachScope<TState>(Action<object?, TState> callback, TState state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        // A static lambda with the callback in the state, so every log record allocates no closure.
        _inner.ForEachScope(
            static (object? scope, (Action<object?, TState> Callback, TState State) s) =>
                s.Callback(Redact(scope), s.State),
            (Callback: callback, State: state));
    }

    /// <inheritdoc />
    public IDisposable Push(object? state) => _inner.Push(state);

    public void Dispose()
    {
        if (!ownsInner)
            return;

        // An async-only inner provider is disposed here rather than skipped on the synchronous path.
        switch (_inner)
        {
            case IDisposable disposable:
                disposable.Dispose();
                break;
            case IAsyncDisposable asyncDisposable:
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!ownsInner)
            return;

        switch (_inner)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private const string Redacted = "[redacted]";

    private static object? Redact(object? scope)
    {
        // IEnumerable rather than IReadOnlyList, because BeginScope(new Dictionary<,>) is not a list (§13.4).
        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
            return RedactPairs(scope, pairs);

        return SensitiveKeys.LooksLikeSecret(scope) ? Redacted : scope;
    }

    // Scanned before it is copied, so the common case returns the caller's own object.
    private static object RedactPairs(object scope, IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        if (!AnySensitive(pairs))
            return scope;

        List<KeyValuePair<string, object?>> scrubbed = [];

        foreach (KeyValuePair<string, object?> pair in pairs)
        {
            scrubbed.Add(IsSensitive(pair) ? new KeyValuePair<string, object?>(pair.Key, Redacted) : pair);
        }

        return new RedactedScope(scrubbed);
    }

    private static bool AnySensitive(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        foreach (KeyValuePair<string, object?> pair in pairs)
        {
            if (IsSensitive(pair))
                return true;
        }

        return false;
    }

    private static bool IsSensitive(KeyValuePair<string, object?> pair) =>
        SensitiveKeys.Matches(pair.Key) || SensitiveKeys.LooksLikeSecret(pair.Value);

    /// <summary>Refuses to start a host whose scope provider is not a <see cref="RedactingScopeProvider"/>.</summary>
    private sealed class ScopeRedactionGuard(IExternalScopeProvider scopes) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (scopes is not RedactingScopeProvider)
            {
                throw new InvalidOperationException(
                    $"IExternalScopeProvider resolves to {scopes.GetType().FullName}, not " +
                    $"{nameof(RedactingScopeProvider)}, so §13.4's scope redaction is switched " +
                    "off and every log scope exports raw. A registration made after " +
                    "AddCommonWebDefaults wins, because the container resolves the last one — " +
                    "remove it, or wrap it by calling RedactingScopeProvider" +
                    ".WrapScopesForRedaction after it.");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A scrubbed scope, whose <c>ToString</c> must not print the original values back out.</summary>
    private sealed class RedactedScope(List<KeyValuePair<string, object?>> pairs)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => pairs.Count;

        public KeyValuePair<string, object?> this[int index] => pairs[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => pairs.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();

        public override string ToString() => string.Join(", ", pairs.Select(p => $"{p.Key}={p.Value}"));
    }
}
