namespace Ordering.Infrastructure.Messaging;

/// <summary>The scheduled messages §9.6's saga arms, one per wait.</summary>
/// <remarks>Not contracts, and one type per wait, since MassTransit correlates a schedule by type (§9.6).</remarks>
public sealed record StockReservationExpired(Guid OrderId);

/// <inheritdoc cref="StockReservationExpired"/>
public sealed record PaymentAuthorisationExpired(Guid OrderId);

/// <summary>The acknowledgement of a <c>ConfirmOrder</c> did not arrive; the saga argues its delay.</summary>
public sealed record ConfirmationExpired(Guid OrderId);

/// <inheritdoc cref="StockReservationExpired"/>
public sealed record DespatchExpired(Guid OrderId);

/// <inheritdoc cref="StockReservationExpired"/>
public sealed record StockReleaseExpired(Guid OrderId);
