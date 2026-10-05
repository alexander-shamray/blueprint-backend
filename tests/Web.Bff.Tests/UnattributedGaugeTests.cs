using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Web.Bff.Observability;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The age of the oldest row no Ordering event has attributed, read by the registered clock.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class UnattributedGaugeTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void An_empty_projection_reads_zero() =>
        ReadGauge(DateTimeOffset.UtcNow).ShouldBe(0);

    [Fact]
    public async Task An_unowned_row_reads_as_its_age()
    {
        await fixture.DeliverAsync(OrderEvents.Authorised(Guid.CreateVersion7(), At));

        ReadGauge(DateTimeOffset.UtcNow.AddMinutes(10)).ShouldBeInRange(570, 630);
    }

    [Fact]
    public async Task An_owned_row_is_not_counted()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Authorised(order, At));
        await fixture.DeliverAsync(OrderEvents.Placed(order, Guid.CreateVersion7(), At));

        ReadGauge(DateTimeOffset.UtcNow.AddMinutes(10)).ShouldBe(0, "an Ordering event attributed the row");
    }

    [Fact]
    public async Task A_server_that_never_answers_costs_the_read_its_bound_rather_than_the_drivers_default()
    {
        // Accepts and never answers, so the open waits out its Connect Timeout; the string sets none.
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        Task<Socket> held = silent.AcceptSocketAsync(TestContext.Current.CancellationToken).AsTask();
        string unanswered = $"Server=tcp:127.0.0.1,{((IPEndPoint)silent.LocalEndpoint).Port};Encrypt=False";
        using ProjectionStats stats = new(new SqlConnectionFactory(unanswered), TimeProvider.System);

        Stopwatch elapsed = Stopwatch.StartNew();
        Should.Throw<SqlException>(() => stats.UnattributedAgeSeconds());
        elapsed.Stop();

        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "SqlClient's default open is fifteen seconds");
        (await held).Dispose();
    }

    /// <summary>One reading over this suite's own stats reader and clock.</summary>
    private double ReadGauge(DateTimeOffset now)
    {
        // The factory has to outlive the collection: a Meter disposed with its factory publishes nothing.
        using ServiceProvider provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        IMeterFactory factory = provider.GetRequiredService<IMeterFactory>();

        using ProjectionStats stats = new(new SqlConnectionFactory(fixture.ConnectionString), new FrozenClock(now));
        ProjectionMetrics metrics = new(factory, stats, NullLogger<ProjectionMetrics>.Instance);
        metrics.ShouldNotBeNull();

        Meter mine = factory.Create(ProjectionMetrics.MeterName);
        List<double> measured = [];
        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, mine) && instrument.Name == "bff.orders.unattributed")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => measured.Add(value));

        listener.Start();
        listener.RecordObservableInstruments();

        // Fails closed: with nothing enabled there is no reading to assert over.
        return measured.ShouldHaveSingleItem("the listener enabled no unattributed gauge");
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
