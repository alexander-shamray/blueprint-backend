namespace Notifications.Infrastructure.Observability;

/// <summary>The questions <see cref="NotificationMetrics"/>' gauges ask of the notification log.</summary>
public interface INotificationStats
{
    /// <summary>Pending notices past their first backoff, for every step in <see cref="WaitingSteps.All"/>.</summary>
    IReadOnlyDictionary<string, int> WaitingByStep();

    /// <summary>How long the longest-due claimable notice has waited past two ticks; zero when none has.</summary>
    double OverdueSeconds();
}
