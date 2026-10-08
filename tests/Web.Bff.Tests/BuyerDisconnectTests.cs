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
    /// <summary>The hosting layer's category, whose "Request finished" line closes every request it served.</summary>
    private const string Hosting = "Microsoft.AspNetCore.Hosting.Diagnostics";

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
        // Longer than PricingHop's timeouts, so only the buyer, leaving well inside AttemptTimeout, ends the call.
        _catalog.HangFor = TimeSpan.FromSeconds(30);

        await using WebApplicationFactory<Program> host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging =>
            {
                logging.AddProvider(_logs);
                logging.AddFilter<LogRecorder>(Hosting, LogLevel.Information);
            }));
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "customer-1");
        using CancellationTokenSource buyer =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<HttpResponseMessage> quote = client.PostQuote("GBP", buyer.Token, (Chair, 1));
        await Until(() => _catalog.Calls.Count > 0);
        await buyer.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => quote);

        // The request's own closing line, so every line the host logs for it is in before the reads below.
        await Until(() => _logs.Finished().Any());

        _logs.Finished().ShouldHaveSingleItem().ShouldContain(" - 499 ", Case.Sensitive, "the host's aborted request");
        _logs.RequestErrors().ShouldBeEmpty("a buyer leaving is no upstream fault, so nothing is logged as one");
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(25, TestContext.Current.CancellationToken);

        condition().ShouldBeTrue("the awaited condition never held");
    }

    /// <summary>Every line the host logs, by category, level and message.</summary>
    private sealed class LogRecorder : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _lines = new();

        public IEnumerable<string> Finished() =>
            _lines
                .Where(line =>
                    line.Category == Hosting && line.Message.StartsWith("Request finished", StringComparison.Ordinal))
                .Select(line => line.Message);

        // ASP.NET Core's own categories, where a request answered as an unhandled 500 is logged. The host's other
        // work, such as a gauge whose database is unreachable here, logs errors of its own while the test runs.
        public IEnumerable<string> RequestErrors() =>
            _lines
                .Where(line =>
                    line.Level >= LogLevel.Error &&
                    line.Category.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal))
                .Select(line => $"{line.Category}: {line.Message}");

        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class Recorder(string category, ConcurrentQueue<(string, LogLevel, string)> lines) : ILogger
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
                lines.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
