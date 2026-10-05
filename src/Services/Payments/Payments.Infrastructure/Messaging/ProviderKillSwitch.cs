using MassTransit.Transports.Components;
using Payments.Application.Provider;
using Payments.Infrastructure.Provider;

namespace Payments.Infrastructure.Messaging;

/// <summary>Stops a receive endpoint while the provider is unreachable, so its messages wait (§9.7).</summary>
/// <remarks>
/// Only <see cref="PaymentProviderUnavailableException"/> counts: a terminal fault is the message's own defect, and
/// counting it would close a healthy endpoint. A stopped endpoint reports Degraded, which readiness answers 200.
/// </remarks>
public static class ProviderKillSwitch
{
    /// <summary>Messages a window must exceed first, sized to saga pace, which never reaches the default.</summary>
    public const int ActivationThreshold = 4;

    /// <summary>A percentage: half the window's messages exhausted their retries on an unreachable provider.</summary>
    public const int TripThreshold = 50;

    /// <summary>Outlasts any message's retry ladder, so a fault is counted in the window its attempt opened.</summary>
    public static readonly TimeSpan TrackingPeriod = RetryPolicy.MaxInterval * RetryPolicy.RetryLimit;

    /// <summary>Past <see cref="ProviderHop.CircuitBreakerBreakDuration"/>, so a restart meets a probe.</summary>
    public static readonly TimeSpan RestartTimeout = ProviderHop.CircuitBreakerBreakDuration * 2;

    public static void Configure(KillSwitchOptions options) =>
        options
            .SetActivationThreshold(ActivationThreshold)
            .SetTripThreshold(TripThreshold)
            .SetTrackingPeriod(TrackingPeriod)
            .SetRestartTimeout(RestartTimeout)
            .SetExceptionFilter(f => f.Handle<PaymentProviderUnavailableException>());
}
