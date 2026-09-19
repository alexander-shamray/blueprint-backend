using Common.Application;

namespace Payments.Application.Intents.AuthorisePayment;

/// <summary>
/// §3.2's Accepts column. No payer: it is derived from the record (ADR-028).
/// The amount and currency are the instruction, checked against the record.
/// </summary>
public sealed record AuthorisePaymentCommand(Guid OrderId, decimal Amount, string Currency) : ICommand<Result>;
