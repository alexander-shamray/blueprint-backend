using System.Text.Encodings.Web;
using System.Text.Json;
using Common.Application;
using Grpc.Core;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Application.Carrier;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Fulfilment;
using Shipping.TestSupport;
using Shouldly;
using Xunit;
using MessagingRegistration = Shipping.Infrastructure.Messaging.DependencyInjection;

namespace Shipping.Worker.Tests;

/// <summary>
/// The fulfilment worker end to end over the collection's SQL Server and
/// broker, the simulator's carrier and the Ordering stub (§12.4): each case
/// drives one pass and reads what it left in the engine and in the journal.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ShipmentFulfilmentTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DeliveryAddress Kazakh =
        new("Абай даңғылы 1, ә ғ қ ң ө ұ ү һ і", "пәтер 12", "Алматы", "050000", "KZ");

    private readonly FulfilmentSteps _steps = new(fixture);

    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Every_event_in_the_consumes_column_is_bound_on_the_queue()
    {
        // The in-memory harness replaces the UsingRabbitMq callback where
        // ConfigureConsumer is declared, so the binding is provable only
        // against a real broker. Healthy first, because an endpoint declares
        // its bindings as it starts and the host does not wait for that (§13.5).
        BusHealthStatus health = await fixture.Factory.Services.GetRequiredService<IBusControl>()
            .WaitForHealthStatus(BusHealthStatus.Healthy, FulfilmentSteps.Deadline);
        health.ShouldBe(BusHealthStatus.Healthy);

        string[] bound = await fixture.BindingsAsync(MessagingRegistration.EventsQueue);

        bound.ShouldContain("Common.Contracts.Ordering.V1:OrderConfirmed");
        bound.ShouldContain("Common.Contracts.Ordering.V1:OrderCancelled");
    }

    [Fact]
    public async Task A_confirmed_order_is_booked_and_the_address_round_trips_to_the_carrier()
    {
        Guid order = await _steps.ConfirmAsync(Kazakh);

        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1);

        (await _steps.StatusAsync(order)).ShouldBe("Booked");
        (await _steps.ReferenceAsync(order)).ShouldBe("crr_SIM-OK");
        (await _steps.LockedUntilAsync(order)).ShouldBeNull(
            "the pass that claimed the row released it through Shipment.ReleaseClaim");
        BookingBody().ShouldContain("ә ғ қ ң ө ұ ү һ і", Case.Sensitive,
            "nvarchar end to end: a Cyrillic code page anywhere in the path would answer question marks");
        BookingBody().ShouldContain("пәтер 12");
        BookingBody().ShouldContain("Алматы");
    }

    [Fact]
    public async Task No_log_line_holds_the_address()
    {
        Guid first = await _steps.ConfirmAsync(Kazakh);
        Guid second = await _steps.ConfirmAsync(Kazakh);

        // One owner outage ahead of the bookings, so a pass logs its failure
        // with the exception and the same capture holds both halves: the
        // exception's text and the lines of the passes the address went through.
        fixture.Ordering.Fail(StatusCode.Unavailable);

        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0, "the owner's outage fails the first row claimed");
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1);
        await _steps.ClearBackoffAsync(first);
        await _steps.ClearBackoffAsync(second);
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1, "the row that failed is booked once the owner answers");

        // Each part raw and as a JSON body carries it, because the adapter's
        // serialiser escapes every non-ASCII character: a body quoted into an
        // exception would hold only the escaped form.
        string[] parts = ["Абай", "пәтер", "Алматы"];
        string[] needles =
            [.. parts, .. parts.Select(p => JsonEncodedText.Encode(p, JavaScriptEncoder.Default).ToString())];

        fixture.CapturedLogs.Everything.ShouldNotContain(
            line => needles.Any(needle => line.Contains(needle, StringComparison.Ordinal)));
        fixture.CapturedLogs.Everything.ShouldContain(
            line => line.Contains(FulfilmentSteps.BookingPath, StringComparison.Ordinal),
            "a capture that missed the booking the address travelled on would assert nothing");
        fixture.CapturedLogs.Everything.ShouldContain(
            line => line.StartsWith("Grpc.Core.RpcException", StringComparison.Ordinal),
            "a capture that dropped the exception's text would search none of it");
    }

    [Fact]
    public async Task An_owner_that_refuses_leaves_the_shipment_pending_and_nothing_in_an_error_queue()
    {
        Guid order = await _steps.ConfirmAsync(Kazakh);

        // One refusal, not two, and the count is the arrangement: the stub
        // dequeues one status per call, the claim takes one row, and a gRPC
        // status is asked exactly once because it travels as an HTTP 200 with
        // grpc-status in the trailers. Two queued would make the recovery pass
        // below consume the second and fail.
        fixture.Ordering.Fail(StatusCode.PermissionDenied);

        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0);

        (await _steps.StatusAsync(order)).ShouldBe("Pending");
        (await _steps.AttemptsAsync(order)).ShouldBe(1);
        (await fixture.QueueDepthAsync($"{MessagingRegistration.EventsQueue}_error")).ShouldBe(
            0, "the consumers made no call, so an outage reaches no queue at all");

        await _steps.ClearBackoffAsync(order);
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1, "the stub recovered and the row was still there");
        (await _steps.StatusAsync(order)).ShouldBe("Booked");
        (await _steps.AttemptsAsync(order)).ShouldBe(0, "a released claim resets the backoff");
    }

    [Fact]
    public async Task An_order_the_owner_does_not_know_is_unfulfillable_and_is_not_retried()
    {
        Guid order = await _steps.ConfirmAsync(address: null);

        await fixture.RunFulfilmentPassAsync();

        (await _steps.StatusAsync(order)).ShouldBe("Unfulfillable");
        (await _steps.ReasonAsync(order)).ShouldBe("no_such_order");

        await _steps.ClearBackoffAsync(order);
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0, "a terminal row is outside the claim");
    }

    [Fact]
    public async Task A_carrier_that_refuses_the_address_makes_the_shipment_unfulfillable()
    {
        Guid order = await _steps.ConfirmAsync(Kazakh with { PostalCode = "SIM-REFUSED" });

        await fixture.RunFulfilmentPassAsync();

        (await _steps.StatusAsync(order)).ShouldBe("Unfulfillable");
        (await _steps.ReasonAsync(order)).ShouldBe("address_not_serviceable");
    }

    [Fact]
    public async Task A_crash_between_the_carriers_answer_and_the_commit_books_once_at_the_carrier()
    {
        Guid order = await _steps.ConfirmAsync(Kazakh);
        using CommitFault fault = fixture.FailNextCommit();

        // Zero rather than a throw: the fault is not transient, so the
        // execution strategy hands it on instead of retrying the unit, and the
        // pass catches per row and backs it off — a failed commit reaches the
        // row's catch and never the caller (spec, section 4). The counter is
        // what says the catch ran.
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0);
        (await _steps.AttemptsAsync(order)).ShouldBe(1);

        await _steps.ClearBackoffAsync(order);
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1);

        fault.Fired.ShouldBeTrue("the first pass booked and then failed its commit");
        FulfilmentSteps.BookingCalls(fixture.Carrier).ShouldBe(
            2, "the second pass repeated the call rather than skipping it");
        BookingKeys().Distinct().ShouldHaveSingleItem().ShouldBe($"book:{await _steps.ShipmentIdAsync(order)}");
        (await _steps.ReferenceAsync(order)).ShouldBe("crr_SIM-OK");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.DeliveryAddresses WHERE OrderId = {0}", order)).ShouldBe(1);
    }

    [Fact]
    public async Task A_lapsed_lease_is_taken_by_another_pass()
    {
        Guid order = await _steps.ConfirmAsync(Kazakh);
        await fixture.ExecuteAsync(
            "UPDATE shipping.Shipments SET LockedUntil = DATEADD(second, -1, SYSDATETIMEOFFSET()) WHERE OrderId = {0};",
            order);

        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1, "a lease in the past is no lease");
    }

    [Fact]
    public async Task A_pass_that_throws_leaves_the_host_running()
    {
        // The claim failing, and not a row: RunOnceAsync catches per row, so a
        // carrier outage never reaches ExecuteAsync's catch at all and a test
        // driven through one would stay green with that catch deleted. An
        // unreachable database is what makes the pass itself throw.
        using ShippingWorkerFactory broken = new(Unreachable.Sql, Unreachable.Rabbit);
        FulfilmentWorker worker = broken.Services.GetRequiredService<FulfilmentWorker>();

        await Should.ThrowAsync<Exception>(() => worker.RunOnceAsync(TestContext.Current.CancellationToken));

        await worker.StartAsync(TestContext.Current.CancellationToken);

        // Staged on the loop's own line rather than on a sleep: the direct
        // call above logs nothing, so the line is the loop having ticked and
        // caught. A loop that let the fault out completes instead.
        await FulfilmentSteps.WaitUntil(() =>
            Task.FromResult(ClaimFailedLogged(broken) || worker.ExecuteTask!.IsCompleted));

        // ExecuteTask is the loop, and a faulted one is the host on its way
        // down: the default BackgroundServiceExceptionBehavior stops it.
        worker.ExecuteTask!.IsFaulted.ShouldBeFalse();
        ClaimFailedLogged(broken).ShouldBeTrue();

        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancel_then_despatch_voids_a_pending_shipment_and_never_books_it()
    {
        Guid order = await _steps.ConfirmAsync(Kazakh);

        await _steps.PublishAsync(FulfilmentSteps.Cancelled(order));

        (await _steps.StatusAsync(order)).ShouldBe("Voided");
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0);
        FulfilmentSteps.BookingCalls(fixture.Carrier).ShouldBe(
            0, "spec section 6: a Pending shipment is voided at once and is never booked");
    }

    [Fact]
    public async Task Cancel_before_confirm_leaves_a_tombstone_the_confirmation_finds()
    {
        Guid order = Guid.CreateVersion7();
        fixture.Ordering.Addresses[order] = FulfilmentSteps.Stub(Kazakh);

        await _steps.PublishAsync(FulfilmentSteps.Cancelled(order));
        await _steps.PublishAsync(FulfilmentSteps.Confirmed(order));

        (await _steps.StatusAsync(order)).ShouldBe("Voided");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.Shipments WHERE OrderId = {0}", order)).ShouldBe(1);
    }

    [Fact]
    public async Task A_cancellation_of_a_booked_shipment_asks_the_carrier_and_voids_it()
    {
        Guid order = await _steps.ConfirmAsync(Kazakh);
        await fixture.RunFulfilmentPassAsync();

        await _steps.PublishAsync(FulfilmentSteps.Cancelled(order));
        (await _steps.StatusAsync(order)).ShouldBe("Booked", "tracking continues until the carrier answers");

        await fixture.RunFulfilmentPassAsync();

        (await _steps.StatusAsync(order)).ShouldBe("Voided");
        CancelKeys().ShouldHaveSingleItem().ShouldBe($"cancel:{await _steps.ShipmentIdAsync(order)}");
    }

    [Fact]
    public async Task A_carrier_that_says_it_is_too_late_stamps_the_refusal_and_leaves_it_booked()
    {
        Guid order = await _steps.ConfirmAsync(Kazakh with { PostalCode = "SIM-LATE" });
        await fixture.RunFulfilmentPassAsync();
        await _steps.PublishAsync(FulfilmentSteps.Cancelled(order));

        await fixture.RunFulfilmentPassAsync();

        (await _steps.StatusAsync(order)).ShouldBe("Booked");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.Shipments WHERE OrderId = {0} AND CancellationRefusedAt IS NOT NULL",
            order)).ShouldBe(1, "the saga's review row is the record of the disagreement");
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0, "a refused cancellation is outside the claim");
    }

    [Fact]
    public async Task Despatch_then_cancel_is_a_no_op_and_the_goods_move()
    {
        Guid order = await _steps.ConfirmAsync(Kazakh);
        await fixture.RunFulfilmentPassAsync();
        (await DespatchAsync(order)).ShouldBeTrue();

        // A second copy of the same scan on a fresh scope: the load by order
        // has to bring the tracking rows, or the repeat is a second insert of
        // the key rather than the no-op a repeated scan is (spec, section 5).
        (await DespatchAsync(order)).ShouldBeFalse();

        await _steps.PublishAsync(FulfilmentSteps.Cancelled(order));

        (await _steps.StatusAsync(order)).ShouldBe("Dispatched");
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0, "a despatched shipment is outside the claim");
        CancelKeys().ShouldBeEmpty("the carrier is told nothing about a parcel already collected");
    }

    private static bool ClaimFailedLogged(ShippingWorkerFactory host) =>
        host.CapturedLogs.Everything.Any(line => line.StartsWith("Fulfilment claim failed", StringComparison.Ordinal));

    // Parsed rather than searched: the adapter's serialiser escapes every
    // non-ASCII character, so the raw body holds none of the address's text.
    private string BookingBody()
    {
        string body = fixture.Carrier.LogEntries
            .Single(e => e.RequestMessage!.Path == FulfilmentSteps.BookingPath)
            .RequestMessage!.Body!;

        using JsonDocument sent = JsonDocument.Parse(body);
        JsonElement address = sent.RootElement.GetProperty("address");

        return string.Join(
            '\n',
            address.GetProperty("line1").GetString(),
            address.GetProperty("line2").GetString(),
            address.GetProperty("city").GetString());
    }

    private string[] BookingKeys() => Keys(path => path == FulfilmentSteps.BookingPath);

    private string[] CancelKeys() => Keys(path => path.EndsWith("/cancel", StringComparison.Ordinal));

    private string[] Keys(Func<string, bool> path) =>
    [
        .. fixture.Carrier.LogEntries
            .Where(e => path(e.RequestMessage!.Path))
            .Select(e => e.RequestMessage!.Headers!["Idempotency-Key"].Single())
    ];

    /// <summary>
    /// Records the carrier's collection scan through the repository, as the
    /// tracking worker will, and returns whether it moved the shipment.
    /// </summary>
    private async Task<bool> DespatchAsync(Guid order)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IShipmentRepository shipments = scope.ServiceProvider.GetRequiredService<IShipmentRepository>();

        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                Shipment shipment = await shipments.GetByOrderAsync(new OrderId(order), ct)
                    ?? throw new InvalidOperationException("The shipment the test confirmed is absent.");

                DateTimeOffset now = DateTimeOffset.UtcNow;
                bool moved = shipment.Record("evt-collected", TrackingStatus.Collected, now, now);

                await unitOfWork.SaveChangesAsync(ct);

                return moved;
            },
            TestContext.Current.CancellationToken);
    }
}
