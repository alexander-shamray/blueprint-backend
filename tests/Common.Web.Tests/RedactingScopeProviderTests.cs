using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>§13.4's scope half, the channel <see cref="SensitiveDataRedactor"/> can read and cannot rewrite.</summary>
public class RedactingScopeProviderTests
{
    private static RedactingScopeProvider Provider() => new(new LoggerExternalScopeProvider());

    private static List<object?> ScopesOf(IExternalScopeProvider provider)
    {
        List<object?> seen = [];

        provider.ForEachScope((scope, state) => state.Add(scope), seen);

        return seen;
    }

    // Through the enumerable interface a logging provider reads, as keyed scopes are Dictionaries.
    private static List<KeyValuePair<string, object?>> Pairs(object? scope) =>
        [.. (IEnumerable<KeyValuePair<string, object?>>)scope!];

    [Fact]
    public void A_sensitive_key_in_a_scope_is_redacted()
    {
        RedactingScopeProvider provider = Provider();

        using IDisposable _ = provider.Push(
            new Dictionary<string, object?> { ["Password"] = "hunter2", ["User"] = "ada" });

        IReadOnlyList<KeyValuePair<string, object?>> pairs = Pairs(ScopesOf(provider).Single());

        pairs.Single(p => p.Key == "Password").Value.ShouldBe("[redacted]");
        pairs.Single(p => p.Key == "User").Value.ShouldBe("ada");
    }

    [Fact]
    public void A_connection_string_in_a_scope_is_redacted_by_its_value()
    {
        // The key names nothing sensitive, and a scope is inherited by every record inside it.
        RedactingScopeProvider provider = Provider();

        using IDisposable _ = provider.Push(
            new Dictionary<string, object?>
            {
                // Spaced separator: valid ADO.NET, and invisible to a literal "password=" check.
                ["Dsn"] = "Server=sql,1433;Database=Catalog;User Id=sa;Password = hunter2"
            });

        Pairs(ScopesOf(provider).Single()).Single().Value.ShouldBe("[redacted]");
    }

    [Fact]
    public void A_scope_with_nothing_sensitive_is_passed_through_unchanged()
    {
        // Identity, not equality: the no-match path is every scope on every record.
        RedactingScopeProvider provider = Provider();
        Dictionary<string, object?> original = new() { ["RequestType"] = "PlaceOrderCommand" };

        using IDisposable _ = provider.Push(original);

        ScopesOf(provider).Single().ShouldBeSameAs(original);
    }

    [Fact]
    public void A_redacted_scope_does_not_render_the_secret_from_ToString()
    {
        // A provider that formats a scope rather than enumerating it would print the value back out.
        RedactingScopeProvider provider = Provider();

        using IDisposable _ = provider.Push(new Dictionary<string, object?> { ["Token"] = "hunter2" });

        ScopesOf(provider).Single()!.ToString()!.ShouldNotContain("hunter2");
    }

    [Fact]
    public void An_unkeyed_scope_carrying_a_secret_is_replaced()
    {
        // A string scope reaches an exporter as one unkeyed value, so only the value check applies.
        RedactingScopeProvider provider = Provider();

        using IDisposable _ = provider.Push("Server=sql;User Id=sa;Password=hunter2");

        ScopesOf(provider).Single().ShouldBe("[redacted]");
    }

    [Fact]
    public void An_unkeyed_ordinary_scope_survives()
    {
        RedactingScopeProvider provider = Provider();

        using IDisposable _ = provider.Push("checkout");

        ScopesOf(provider).Single().ShouldBe("checkout");
    }

    [Fact]
    public void Nesting_is_preserved()
    {
        // A wrapper that flattened the stack would drop §13.3's RequestType and §10.4's CorrelationId.
        RedactingScopeProvider provider = Provider();

        using IDisposable outer = provider.Push(new Dictionary<string, object?> { ["A"] = "1" });
        using IDisposable inner = provider.Push(new Dictionary<string, object?> { ["Secret"] = "2" });

        List<object?> seen = ScopesOf(provider);

        seen.Count.ShouldBe(2);
        Pairs(seen[0]).Single().Value.ShouldBe("1");
        Pairs(seen[1]).Single().Value.ShouldBe("[redacted]");
    }

    /// <summary>Captures whatever scope provider the logger factory hands a provider.</summary>
    private sealed class ScopeCapturingProvider : ILoggerProvider, ISupportExternalScope
    {
        public IExternalScopeProvider? Scopes { get; private set; }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => Scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

        public void Dispose()
        {
        }
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("factory")]
    [InlineData("type")]
    public void A_provider_registered_first_is_wrapped_rather_than_left_alone(string shape)
    {
        // §13.4 is a guarantee, so an earlier registration is wrapped, in each of the three descriptor shapes.
        ServiceCollection services = new();

        switch (shape)
        {
            case "instance":
                services.AddSingleton<IExternalScopeProvider>(new LoggerExternalScopeProvider());
                break;
            case "factory":
                services.AddSingleton<IExternalScopeProvider>(_ => new LoggerExternalScopeProvider());
                break;
            default:
                services.AddSingleton<IExternalScopeProvider, LoggerExternalScopeProvider>();
                break;
        }

        RedactingScopeProvider.WrapScopesForRedaction(services);

        using ServiceProvider root = services.BuildServiceProvider();
        IExternalScopeProvider resolved = root.GetRequiredService<IExternalScopeProvider>();

        resolved.ShouldBeOfType<RedactingScopeProvider>(
            $"a {shape} registration made first must be wrapped, not deferred to");

        using IDisposable _ = resolved.Push(
            new Dictionary<string, object?> { ["Password"] = "hunter2" });

        Pairs(ScopesOf(resolved).Single()).Single().Value.ShouldBe("[redacted]");
    }

    [Fact]
    public async Task A_provider_registered_afterwards_stops_the_host()
    {
        // A later registration would replace the wrapper, and no earlier one can prevent that,
        // so the host refuses to start.
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddObservability();
        builder.Services.AddSingleton<IExternalScopeProvider>(new LoggerExternalScopeProvider());

        using IHost host = builder.Build();

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain(nameof(RedactingScopeProvider));
    }

    [Fact]
    public async Task A_host_whose_provider_is_the_wrapper_starts()
    {
        // The control, without which the test above passes against a guard that refuses every host.
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddObservability();

        using IHost host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        host.Services
            .GetRequiredService<IExternalScopeProvider>()
            .ShouldBeOfType<RedactingScopeProvider>();
    }

    /// <summary>A provider that records whether it was disposed.</summary>
    private sealed class DisposableScopeProvider : IExternalScopeProvider, IDisposable
    {
        private readonly LoggerExternalScopeProvider _inner = new();

        public bool Disposed { get; private set; }

        public void ForEachScope<TState>(Action<object?, TState> callback, TState state) =>
            _inner.ForEachScope(callback, state);

        public IDisposable Push(object? state) => _inner.Push(state);

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void A_provider_this_wrapper_built_is_disposed_with_the_container()
    {
        // Wrapping removes the prior descriptor, so the wrapper must dispose what the container built.
        DisposableScopeProvider built = new();
        ServiceCollection services = new();

        services.AddSingleton<IExternalScopeProvider>(_ => built);
        RedactingScopeProvider.WrapScopesForRedaction(services);

        ServiceProvider root = services.BuildServiceProvider();
        root.GetRequiredService<IExternalScopeProvider>().ShouldBeOfType<RedactingScopeProvider>();

        root.Dispose();

        built.Disposed.ShouldBeTrue("a provider this wrapper constructed is this wrapper's to dispose");
    }

    /// <summary>A provider that can only be disposed asynchronously.</summary>
    private sealed class AsyncOnlyScopeProvider : IExternalScopeProvider, IAsyncDisposable
    {
        private readonly LoggerExternalScopeProvider _inner = new();

        public bool Disposed { get; private set; }

        public void ForEachScope<TState>(Action<object?, TState> callback, TState state) =>
            _inner.ForEachScope(callback, state);

        public IDisposable Push(object? state) => _inner.Push(state);

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task An_async_only_provider_this_wrapper_built_is_disposed()
    {
        // A provider implementing only `IAsyncDisposable` leaves DI tracking too.
        AsyncOnlyScopeProvider built = new();
        ServiceCollection services = new();

        services.AddSingleton<IExternalScopeProvider>(_ => built);
        RedactingScopeProvider.WrapScopesForRedaction(services);

        ServiceProvider root = services.BuildServiceProvider();
        root.GetRequiredService<IExternalScopeProvider>().ShouldBeOfType<RedactingScopeProvider>();

        await root.DisposeAsync();

        built.Disposed.ShouldBeTrue("an async-only provider is still this wrapper's to dispose");
    }

    [Fact]
    public void An_async_only_provider_is_disposed_on_the_synchronous_path_too()
    {
        // The container may take either path, so Dispose must reach an async-only provider too.
        AsyncOnlyScopeProvider built = new();
        ServiceCollection services = new();

        services.AddSingleton<IExternalScopeProvider>(_ => built);
        RedactingScopeProvider.WrapScopesForRedaction(services);

        ServiceProvider root = services.BuildServiceProvider();
        root.GetRequiredService<IExternalScopeProvider>().ShouldBeOfType<RedactingScopeProvider>();

        root.Dispose();

        built.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void A_provider_the_caller_supplied_is_left_alone()
    {
        // The container never disposes an instance it did not create, so neither may the wrapper.
        DisposableScopeProvider supplied = new();
        ServiceCollection services = new();

        services.AddSingleton<IExternalScopeProvider>(supplied);
        RedactingScopeProvider.WrapScopesForRedaction(services);

        ServiceProvider root = services.BuildServiceProvider();
        root.GetRequiredService<IExternalScopeProvider>().ShouldBeOfType<RedactingScopeProvider>();

        root.Dispose();

        supplied.Disposed.ShouldBeFalse("an instance the container never created is not the wrapper's to dispose");
    }

    [Fact]
    public void The_wrapper_delegates_to_the_provider_it_replaced()
    {
        // Wrapping must not discard the host's own provider.
        ServiceCollection services = new();
        RecordingScopeProvider inner = new();

        services.AddSingleton<IExternalScopeProvider>(inner);
        RedactingScopeProvider.WrapScopesForRedaction(services);

        using ServiceProvider root = services.BuildServiceProvider();

        using IDisposable _ = root
            .GetRequiredService<IExternalScopeProvider>()
            .Push(new Dictionary<string, object?> { ["RequestType"] = "PlaceOrderCommand" });

        inner.Pushed.ShouldHaveSingleItem();
    }

    /// <summary>Records what was pushed, implementing the interface as <c>Push</c> is not virtual.</summary>
    private sealed class RecordingScopeProvider : IExternalScopeProvider
    {
        private readonly LoggerExternalScopeProvider _inner = new();

        public List<object?> Pushed { get; } = [];

        public void ForEachScope<TState>(Action<object?, TState> callback, TState state) =>
            _inner.ForEachScope(callback, state);

        public IDisposable Push(object? state)
        {
            Pushed.Add(state);

            return _inner.Push(state);
        }
    }

    [Fact]
    public void The_logger_factory_hands_providers_the_registered_scope_provider()
    {
        // Everything above rests on the container choosing LoggerFactory's constructor that takes an
        // IExternalScopeProvider, which is how the wrapper reaches OpenTelemetry's provider at all.
        ServiceCollection services = new();
        ScopeCapturingProvider capture = new();

        services.AddSingleton<IExternalScopeProvider>(Provider());
        services.AddLogging(logging => logging.AddProvider(capture));

        using ServiceProvider root = services.BuildServiceProvider();
        ILogger logger = root.GetRequiredService<ILoggerFactory>().CreateLogger("test");

        // ILogger.BeginScope is nullable-returning; Push above is not.
        using IDisposable? _ = logger.BeginScope(
            new Dictionary<string, object?> { ["Password"] = "hunter2" });

        IExternalScopeProvider handed = capture.Scopes.ShouldNotBeNull();

        Pairs(ScopesOf(handed).Single()).Single().Value.ShouldBe("[redacted]");
    }
}
