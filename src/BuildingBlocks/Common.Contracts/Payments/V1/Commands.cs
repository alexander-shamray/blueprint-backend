namespace Common.Contracts.Payments.V1;

/// <summary>
/// Authorise a payment for an order (§3.2's Accepts column), sent by the saga
/// to <c>payments-commands</c> (§9.6).
/// </summary>
/// <remarks>
/// The currency travels with the amount (§9.6). No subject is carried: whose
/// instrument is charged is Payments' to derive from its own record of the
/// order, and a <c>CustomerId</c> here would be a second source for that
/// decision (ADR-028). The amount and currency are instructions, not authority.
/// </remarks>
public sealed record AuthorisePayment(Guid OrderId, decimal Amount, string Currency);
