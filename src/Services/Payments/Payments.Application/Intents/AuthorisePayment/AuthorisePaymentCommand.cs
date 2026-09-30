using Common.Application;

namespace Payments.Application.Intents.AuthorisePayment;

/// <summary>§3.2's Accepts column, with no payer, which the record supplies (ADR-028).</summary>
public sealed record AuthorisePaymentCommand(Guid OrderId, decimal Amount, string Currency) : ICommand<Result>;
