using Common.Contracts;
using Common.Contracts.Ordering.V1;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Application.Carrier;
using Shipping.OrderingStub;
using Shipping.TestSupport;
using WireMock.Server;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// What the fulfilment suites arrange and read over the collection's
/// containers: an order confirmed through the real broker, and one column of
/// its shipment read back from the engine.
/// </summary>
internal sealed class FulfilmentSteps(ServiceFixture fixture)
{
    /// <summary>The carrier's booking path, as the adapter posts it.</summary>
    public const string BookingPath = "/v1/shipments";

    /// <summary>
    /// How long any staged step may take: a delivery through the broker, a
    /// bus coming up, or a pass part-way through a stalled carrier answer.
    /// A deadline, not a sleep, so it costs nothing when the step is prompt.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>
    /// An address in a script the booking's JSON escapes, so a search can tell
    /// the raw text from the escaped and nvarchar from a code page.
    /// </summary>
    public static readonly DeliveryAddress Kazakh =
        new("Абай даңғылы 1, ә ғ қ ң ө ұ ү һ і", "пәтер 12", "Алматы", "050000", "KZ");

    /// <summary>
    /// Seeds the address the stub will answer with, or none, publishes the
    /// event that creates the shipment, and waits until the claim can see it.
    /// </summary>
    public async Task<Guid> ConfirmAsync(DeliveryAddress? address)
    {
        Guid order = Guid.CreateVersion7();

        if (address is not null)
            fixture.Ordering.Addresses[order] = Stub(address);

        await PublishAsync(Confirmed(order));
        await fixture.WaitUntilAttemptDueAsync(order);

        return order;
    }

    /// <summary>
    /// Publishes onto the bus and waits for this message's inbox row, which
    /// is written after the handler's command has committed (§9.5): a pass
    /// run before then would claim nothing, and the assertions after it
    /// would be about an empty table.
    /// </summary>
    public async Task PublishAsync<T>(T message)
        where T : class, IIntegrationEvent
    {
        // Bounded, because a publish the broker refuses is retried rather than
        // failed, and an unbounded one would hold the run until CI kills it.
        using CancellationTokenSource bounded =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bounded.CancelAfter(Deadline);

        await fixture.Factory.Services.GetRequiredService<IBus>().Publish(
            message,
            c =>
            {
                c.MessageId = message.MessageId;
                c.CorrelationId = message.CorrelationId;
            },
            bounded.Token);

        await WaitUntil(async () => (await fixture.InboxAsync(message.MessageId)).Count == 1);
    }

    public static StubAddress Stub(DeliveryAddress address) =>
        new(Guid.CreateVersion7(), address.Line1, address.Line2, address.City, address.PostalCode, address.Country);

    public static OrderConfirmed Confirmed(Guid order) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = order,
        CustomerId = Guid.CreateVersion7(),
        TotalAmount = 10m,
        Currency = "KZT",
        Lines = [new ConfirmedLine(Guid.CreateVersion7(), 1, 10m)]
    };

    public static OrderCancelled Cancelled(Guid order) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = order,
        CustomerId = Guid.CreateVersion7(),
        Reason = CancelReasons.CustomerRequest
    };

    public Task<string> StatusAsync(Guid order) =>
        fixture.ScalarAsync<string>("SELECT Value = Status FROM shipping.Shipments WHERE OrderId = {0}", order);

    public Task<string> ReferenceAsync(Guid order) =>
        fixture.ScalarAsync<string>(
            "SELECT Value = CarrierReference FROM shipping.Shipments WHERE OrderId = {0}", order);

    public Task<string> ReasonAsync(Guid order) =>
        fixture.ScalarAsync<string>(
            "SELECT Value = UnfulfillableReason FROM shipping.Shipments WHERE OrderId = {0}", order);

    public Task<int> AttemptsAsync(Guid order) =>
        fixture.ScalarAsync<int>("SELECT Value = Attempts FROM shipping.Shipments WHERE OrderId = {0}", order);

    public Task<Guid> ShipmentIdAsync(Guid order) =>
        fixture.ScalarAsync<Guid>("SELECT Value = Id FROM shipping.Shipments WHERE OrderId = {0}", order);

    /// <summary>Null once a pass has released the row it claimed.</summary>
    public Task<DateTimeOffset?> LockedUntilAsync(Guid order) =>
        fixture.ScalarAsync<DateTimeOffset?>(
            "SELECT Value = LockedUntil FROM shipping.Shipments WHERE OrderId = {0}", order);

    /// <summary>
    /// The instant itself and not a <c>DATEDIFF</c> from the SQL clock:
    /// <c>DATEDIFF</c> counts the boundaries of its unit crossed, so a
    /// five-second backoff read across a second boundary answers four.
    /// </summary>
    public Task<DateTimeOffset> NextAttemptAtAsync(Guid order) =>
        fixture.ScalarAsync<DateTimeOffset>(
            "SELECT Value = NextAttemptAt FROM shipping.Shipments WHERE OrderId = {0}", order);

    /// <summary>Makes a backed-off row claimable now, so a test need not wait out the ladder.</summary>
    public Task ClearBackoffAsync(Guid order) =>
        fixture.ExecuteAsync(
            "UPDATE shipping.Shipments SET NextAttemptAt = SYSDATETIMEOFFSET() WHERE OrderId = {0};", order);

    /// <summary>
    /// The engine's own clock, for a comparison with a column the engine
    /// stamped: the container's clock and the host's can disagree.
    /// </summary>
    public Task<DateTimeOffset> DatabaseNowAsync() =>
        fixture.ScalarAsync<DateTimeOffset>("SELECT Value = SYSDATETIMEOFFSET()");

    public static int BookingCalls(WireMockServer carrier) =>
        carrier.LogEntries.Count(e => e.RequestMessage!.Path == BookingPath);

    /// <summary>
    /// Polls to a deadline and throws when it lapses, which is what stages a
    /// step on something another has already done rather than on a sleep.
    /// </summary>
    public static async Task WaitUntil(Func<Task<bool>> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Deadline;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"The staged condition did not hold within {Deadline}.");
    }
}
