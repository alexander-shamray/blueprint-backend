using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Infrastructure.Observability;
using Shouldly;

namespace Notifications.Worker.Tests;

/// <summary>One host's <c>notifications.mail.resent</c>, counted by <see cref="OutboundCount"/>.</summary>
internal static class ResentCounter
{
    public static OutboundCount Resent(IServiceProvider services)
    {
        // The counter is created in NotificationMetrics' constructor; a listener started first would see nothing.
        services.GetRequiredService<NotificationMetrics>();
        Meter mine = services.GetRequiredService<IMeterFactory>().Create(OutboundMeter.Name);
        OutboundCount count = new(mine, "notifications.mail.resent");
        count.Enabled.ShouldBeTrue("no resend counter on this host's meter was enabled, so a zero would prove nothing");

        return count;
    }
}
