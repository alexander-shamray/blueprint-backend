using Common.Application;

namespace Ordering.Application.Orders.FlagOrderForReview;

/// <summary>Escalate an order to a human, for work this workflow cannot finish itself (§9.6).</summary>
/// <remarks><see cref="Reason"/> is a string with no domain type behind it; the mapper keeps it closed.</remarks>
public sealed record FlagOrderForReviewCommand(Guid OrderId, string Reason) : ICommand<Result>;
