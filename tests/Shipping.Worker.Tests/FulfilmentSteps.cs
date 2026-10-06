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

/// <summary>What the fulfilment suites arrange and read over the collection's containers.</summary>
internal sealed class FulfilmentSteps(ServiceFixture fixture)
{
    /// <summary>The carrier's booking path, as the adapter posts it.</summary>
    public const string BookingPath = "/v1/shipments";

    /// <summary>How long a staged step may take; a deadline, not a sleep, so a prompt step costs nothing.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>An address in a script the booking's JSON escapes, so raw, escaped and code-page text differ.</summary>
    public static readonly DeliveryAddress Kazakh =
        new("Абай даңғылы 1, ә ғ қ ң ө ұ ү һ і", "пәтер 12", "Алматы", "050000", "KZ");

    /// <summary>Seeds the stub's address, or none, publishes the confirmation and waits until it is due.</summary>
    public async Task<Guid> ConfirmAsync(DeliveryAddress? address)
    {
        Guid order = Guid.CreateVersion7();

        if (address is not null)
            fixture.Ordering.Addresses[order] = Stub(address);

        await PublishAsync(Confirmed(order));
        await fixture.WaitUntilAttemptDueAsync(order);

        return order;
    }

    /// <summary>Publishes and waits for this message's inbox row, written once the command commits (§9.5).</summary>
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
            "SELECT Value = CarrierReference FROM shipping.Shipments WHERE OrderId = {0}",
            order);

    public Task<string> ReasonAsync(Guid order) =>
        fixture.ScalarAsync<string>(
            "SELECT Value = UnfulfillableReason FROM shipping.Shipments WHERE OrderId = {0}",
            order);

    public Task<int> AttemptsAsync(Guid order) =>
        fixture.ScalarAsync<int>("SELECT Value = Attempts FROM shipping.Shipments WHERE OrderId = {0}", order);

    public Task<Guid> ShipmentIdAsync(Guid order) =>
        fixture.ScalarAsync<Guid>("SELECT Value = Id FROM shipping.Shipments WHERE OrderId = {0}", order);

    /// <summary>Null once a pass has released the row it claimed.</summary>
    public Task<DateTimeOffset?> LockedUntilAsync(Guid order) =>
        fixture.ScalarAsync<DateTimeOffset?>(
            "SELECT Value = LockedUntil FROM shipping.Shipments WHERE OrderId = {0}",
            order);

    /// <summary>The instant itself, as <c>DATEDIFF</c> counts boundaries and can read five seconds as four.</summary>
    public Task<DateTimeOffset> NextAttemptAtAsync(Guid order) =>
        fixture.ScalarAsync<DateTimeOffset>(
            "SELECT Value = NextAttemptAt FROM shipping.Shipments WHERE OrderId = {0}",
            order);

    /// <summary>Moves <c>CreatedAt</c> back by <paramref name="age"/> from the host's clock.</summary>
    public Task AgeAsync(Guid order, TimeSpan age) =>
        fixture.ExecuteAsync(
            "UPDATE shipping.Shipments SET CreatedAt = {1} WHERE OrderId = {0};",
            order,
            DateTimeOffset.UtcNow - age);

    /// <summary>Makes a backed-off row claimable now, so a test need not wait out the ladder.</summary>
    public Task ClearBackoffAsync(Guid order) =>
        fixture.ExecuteAsync(
            "UPDATE shipping.Shipments SET NextAttemptAt = SYSDATETIMEOFFSET() WHERE OrderId = {0};",
            order);

    /// <summary>The engine's own clock, since the container's and the host's can disagree.</summary>
    public Task<DateTimeOffset> DatabaseNowAsync() =>
        fixture.ScalarAsync<DateTimeOffset>("SELECT Value = SYSDATETIMEOFFSET()");

    public static int BookingCalls(WireMockServer carrier) =>
        carrier.LogEntries.Count(e => e.RequestMessage!.Path == BookingPath);

    /// <summary>Polls to <see cref="Deadline"/> and throws when it lapses.</summary>
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
