using Common.Application;

namespace Ordering.Application.Orders.FlagOrderForReview;

/// <summary>§9.6's escalation: an operations row for work the workflow cannot finish, and no aggregate.</summary>
/// <remarks>
/// Written through <see cref="IUnitOfWork"/>, inside §6.3's transaction; <c>IDbConnectionFactory</c> belongs to
/// queries and projections (§6.5, ADR-018). The reason codes are argued in §9.6.
/// </remarks>
public sealed class FlagOrderForReviewHandler(IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<FlagOrderForReviewCommand, Result>
{
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
            new { command.OrderId, command.Reason, RaisedAt = clock.GetUtcNow() },
            ct);

        // RaisedAt from the registered clock, not SYSDATETIMEOFFSET(), so a test host's clock governs it (§9.6).
        return Result.Success();
    }
}
