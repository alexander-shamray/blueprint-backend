using Common.Application;
using Microsoft.Extensions.Logging;
using Ordering.Application.Orders.FlagOrderForReview;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

public class FlagOrderForReviewHandlerTests
{
    private static readonly Guid Order = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly RecordingUnitOfWork _unitOfWork = new();
    private readonly RecordingLog _log = new();

    private FlagOrderForReviewHandler Handler() => new(_unitOfWork, TimeProvider.System, _log);

    [Fact]
    public async Task A_flag_says_the_order_and_the_reason_at_information()
    {
        await Handler().HandleAsync(
            new FlagOrderForReviewCommand(Order, "stock_not_released"),
            TestContext.Current.CancellationToken);

        _log.Lines.ShouldHaveSingleItem()
            .ShouldBe((LogLevel.Information, $"Order {Order} flagged for review: stock_not_released."));
    }

    [Fact]
    public async Task The_line_follows_the_write_so_a_failed_insert_says_nothing()
    {
        _unitOfWork.Fails = true;

        await Should.ThrowAsync<InvalidOperationException>(() => Handler().HandleAsync(
            new FlagOrderForReviewCommand(Order, "stock_not_released"),
            TestContext.Current.CancellationToken));

        _log.Lines.ShouldBeEmpty();
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public bool Fails { get; set; }

        public bool HasActiveTransaction => true;

        public int ModifiedAggregateCount => 0;

        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<int> SaveChangesAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task ExecuteRawAsync(string sql, object parameters, CancellationToken ct) =>
            Fails ? throw new InvalidOperationException("insert failed") : Task.CompletedTask;
    }

    private sealed class RecordingLog : ILogger<FlagOrderForReviewHandler>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }
}
