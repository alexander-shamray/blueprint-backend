using Common.Application;
using Microsoft.Extensions.Logging;

namespace Ordering.Application.Orders.FlagOrderForReview;

/// <summary>§9.6's escalation: an operations row for work the workflow cannot finish, and no aggregate.</summary>
/// <remarks>
/// Written through <see cref="IUnitOfWork"/>, inside §6.3's transaction; <c>IDbConnectionFactory</c> belongs to
/// queries and projections (§6.5, ADR-018). The reason codes are argued in §9.6.
/// </remarks>
public sealed class FlagOrderForReviewHandler(
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<FlagOrderForReviewHandler> log)
    : ICommandHandler<FlagOrderForReviewCommand, Result>
{
    // §13.4's Information line for a business event, compiled once (CA1848, ADR-019); an order id and a code only.
    private static readonly Action<ILogger, Guid, string, Exception?> Flagged =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Information,
            new EventId(1, nameof(Flagged)),
            "Order {OrderId} flagged for review: {Reason}.");

    public async Task<Result> HandleAsync(FlagOrderForReviewCommand command, CancellationToken ct)
    {
        // The lock hints make the read a range lock, so a duplicate delivery is absorbed rather than violating the
        // key; absorbed, not upserted, since RaisedAt is what §13.6 alerts on (§9.6).
        await unitOfWork.ExecuteRawAsync(
            """
            INSERT INTO ordering.OrderReviews (OrderId, Reason, RaisedAt)
            SELECT @OrderId, @Reason, @RaisedAt
            WHERE NOT EXISTS (
                SELECT 1
                FROM ordering.OrderReviews WITH (UPDLOCK, HOLDLOCK)
                WHERE OrderId = @OrderId
                    AND Reason = @Reason);
            """,
            // RaisedAt from the registered clock, not SYSDATETIMEOFFSET(), so a test host's clock governs it (§9.6).
            new { command.OrderId, command.Reason, RaisedAt = clock.GetUtcNow() },
            ct);

        // Said on a redelivery too, since the insert reports no row count; the line is a trace, the row the queue.
        Flagged(log, command.OrderId, command.Reason, null);

        return Result.Success();
    }
}
