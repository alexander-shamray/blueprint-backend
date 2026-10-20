using System.ComponentModel.DataAnnotations;

namespace Ordering.Infrastructure.Messaging;

/// <summary>§9.6's five waits, each defaulting to the saga's own delay and bound from configuration.</summary>
/// <remarks>
/// A lever for a developer's stack, where a wait of minutes or days cannot be watched; no deployed value sets it
/// (§14.1). The defaults stay the saga's constants, which other services and §9.6 cite by name.
/// </remarks>
public sealed class OrderFulfilmentTimeouts
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Saga";

    /// <summary>Shorter, and the timer fires before the broker has delivered the message that would cancel
    /// it.</summary>
    public const string MinimumDelay = "00:00:01";

    /// <summary>Ten years, which is a configuration error rather than a policy.</summary>
    public const string MaximumDelay = "3650.00:00:00";

    /// <summary>Armed in <c>AwaitingStock</c>.</summary>
    [Range(typeof(TimeSpan), MinimumDelay, MaximumDelay, ParseLimitsInInvariantCulture = true)]
    public TimeSpan StockTimeoutDelay { get; init; } = OrderFulfilmentSaga.StockTimeoutDelay;

    /// <summary>Armed in <c>AwaitingPayment</c>.</summary>
    [Range(typeof(TimeSpan), MinimumDelay, MaximumDelay, ParseLimitsInInvariantCulture = true)]
    public TimeSpan PaymentTimeoutDelay { get; init; } = OrderFulfilmentSaga.PaymentTimeoutDelay;

    /// <summary>Armed in <c>AwaitingConfirmation</c>.</summary>
    [Range(typeof(TimeSpan), MinimumDelay, MaximumDelay, ParseLimitsInInvariantCulture = true)]
    public TimeSpan ConfirmationTimeoutDelay { get; init; } = OrderFulfilmentSaga.ConfirmationTimeoutDelay;

    /// <summary>Armed in <c>Compensating</c>, for a <c>ReleaseStock</c> the saga sent.</summary>
    [Range(typeof(TimeSpan), MinimumDelay, MaximumDelay, ParseLimitsInInvariantCulture = true)]
    public TimeSpan ReleaseTimeoutDelay { get; init; } = OrderFulfilmentSaga.ReleaseTimeoutDelay;

    /// <summary>Armed in <c>Confirmed</c>.</summary>
    [Range(typeof(TimeSpan), MinimumDelay, MaximumDelay, ParseLimitsInInvariantCulture = true)]
    public TimeSpan DespatchTimeoutDelay { get; init; } = OrderFulfilmentSaga.DespatchTimeoutDelay;
}
