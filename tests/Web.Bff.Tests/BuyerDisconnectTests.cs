using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Shouldly;
using Web.Bff.TestSupport;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>A buyer who leaves while Catalog prices the basket, which is no upstream failure (§9.7, §13.2).</summary>
public sealed class BuyerDisconnectTests : IAsyncLifetime
{
    private const string ExceptionHandler = "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware";

    private static readonly Guid Chair = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly StubCatalog _catalog = new();
    private readonly LogRecorder _logs = new();

    private BffFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await _catalog.InitializeAsync();

        _factory = new BffFactory { PricingAddress = _catalog.Address };
        _catalog.Prices[Chair] = ("Chair", 49.99m, "GBP");
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _catalog.DisposeAsync();
    }

    [Fact]
    public async Task A_buyer_who_leaves_mid_pricing_is_an_aborted_request_rather_than_a_logged_500()
    {
        // Longer than the test waits, so the only thing that ends the call is the buyer leaving.
        _catalog.HangFor = TimeSpan.FromSeconds(30);

        await using WebApplicationFactory<Program> host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging =>
            {
                logging.AddProvider(_logs);

                // Debug for this provider alone, so the middleware's aborted-request line is seen too.
                logging.AddFilter<LogRecorder>(ExceptionHandler, LogLevel.Debug);
            }));
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "customer-1");
        using CancellationTokenSource buyer =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<HttpResponseMessage> quote = client.PostQuote("GBP", buyer.Token, (Chair, 1));
        await Until(() => _catalog.Calls.Count > 0);
        await buyer.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => quote);

        // The middleware answers every exception it sees with one line, so waiting on it orders the read.
        await Until(() => _logs.From(ExceptionHandler).Any());

        _logs.From(ExceptionHandler).ShouldAllBe(
            level => level < LogLevel.Error,
            "a buyer leaving is the host's aborted request, not an upstream fault counted as a 500");
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(25, TestContext.Current.CancellationToken);

        condition().ShouldBeTrue("the awaited condition never held");
    }

    /// <summary>The level of every line the host logs, by category.</summary>
    private sealed class LogRecorder : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level)> _lines = new();

        public IEnumerable<LogLevel> From(string category) =>
            _lines.Where(line => line.Category == category).Select(line => line.Level);

        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class Recorder(string category, ConcurrentQueue<(string, LogLevel)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                lines.Enqueue((category, logLevel));
        }
    }
}
