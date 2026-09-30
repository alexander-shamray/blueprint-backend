namespace Common.Contracts.Payments.V1;

/// <summary>No subject: whose instrument is charged is Payments' to derive from its own record (ADR-028).</summary>
public sealed record AuthorisePayment(Guid OrderId, decimal Amount, string Currency);
