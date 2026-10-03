using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Infrastructure.Contacts;
using Notifications.Infrastructure.Mail;
using Notifications.Infrastructure.Observability;
using Shouldly;

namespace Notifications.Worker.Tests;

/// <summary>One host's outbound counter, matched by meter instance as a <c>MeterListener</c> is process-wide.</summary>
internal static class OutboundCounter
{
    public static OutboundCount Unavailable(IServiceProvider services)
    {
        services.GetRequiredService<MailMetrics>();
        return Of(services, "notifications.mail.unavailable");
    }

    public static OutboundCount ContactRefused(IServiceProvider services)
    {
        services.GetRequiredService<ContactMetrics>();
        return Of(services, "notifications.contact.refused");
    }

    private static OutboundCount Of(IServiceProvider services, string instrument)
    {
        // The metrics type's constructor creates the counter, so Start publishes it at once and Enabled is checkable.
        Meter mine = services.GetRequiredService<IMeterFactory>().Create(OutboundMeter.Name);
        OutboundCount count = new(mine, instrument);
        count.Enabled.ShouldBeTrue($"no {instrument} on this host's meter was enabled, so a zero would prove nothing");
        return count;
    }
}

internal sealed class OutboundCount : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<string?> _causes = new();

    public OutboundCount(Meter mine, string instrument)
    {
        _listener.InstrumentPublished = (published, l) =>
        {
            if (ReferenceEquals(published.Meter, mine) && published.Name == instrument)
            {
                l.EnableMeasurementEvents(published);
                Enabled = true;
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? cause = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "cause")
                    cause = tag.Value as string;
            }

            for (long i = 0; i < value; i++)
                _causes.Enqueue(cause);
        });
        _listener.Start();
    }

    public bool Enabled { get; private set; }

    public long Value => _causes.Count;

    public long Of(string cause) => _causes.Count(c => c == cause);

    public void Dispose() => _listener.Dispose();
}
