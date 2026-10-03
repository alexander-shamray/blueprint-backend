using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Infrastructure.Mail;
using Notifications.Infrastructure.Observability;
using Shouldly;

namespace Notifications.Worker.Tests;

/// <summary>One host's relay counter, matched by meter instance as a <c>MeterListener</c> is process-wide.</summary>
internal static class MailCounter
{
    public static MailCount Unavailable(IServiceProvider services)
    {
        // The counter is created in MailMetrics' constructor; a listener started first would see nothing published.
        services.GetRequiredService<MailMetrics>();
        Meter mine = services.GetRequiredService<IMeterFactory>().Create(OutboundMeter.Name);
        MailCount count = new(mine);
        count.Enabled.ShouldBeTrue("no counter on this host's meter was enabled, so a zero would prove nothing");
        return count;
    }
}

internal sealed class MailCount : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<string?> _causes = new();

    public MailCount(Meter mine)
    {
        _listener.InstrumentPublished = (published, l) =>
        {
            if (ReferenceEquals(published.Meter, mine) && published.Name == "notifications.mail.unavailable")
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
