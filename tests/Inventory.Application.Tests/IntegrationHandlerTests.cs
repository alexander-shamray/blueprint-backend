using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Shipping.V1;
using Inventory.Application.Reservations.Fulfil;
using Inventory.Application.Reservations.Integration;
using Inventory.Application.Reservations.ReleaseStock;
using Shouldly;
using Xunit;

namespace Inventory.Application.Tests;

/// <summary>
/// The integration handlers end to end against a recording dispatcher: each
/// is one dispatch (§3.2), so the fact worth pinning is what it sent, not a
/// database.
/// </summary>
public class IntegrationHandlerTests
{
    [Fact]
    public async Task A_cancellation_dispatches_a_system_release()
    {
        var dispatcher = new RecordingDispatcher();
        var order = Guid.CreateVersion7();

        await new OrderCancelledHandler(dispatcher).HandleAsync(
            new OrderCancelled
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = order,
                OccurredAt = DateTimeOffset.UtcNow,
                OrderId = order,
                CustomerId = Guid.CreateVersion7(),
                Reason = CancelReasons.CustomerRequest
            },
            CancellationToken.None);

        ReleaseStockCommand command = dispatcher.Commands.OfType<ReleaseStockCommand>().ShouldHaveSingleItem();
        command.OrderId.ShouldBe(order);
        command.Origin.ShouldBe(CommandOrigin.System);
    }

    [Fact]
    public async Task A_despatch_dispatches_a_fulfilment()
    {
        var dispatcher = new RecordingDispatcher();
        var order = Guid.CreateVersion7();

        await new ShipmentDispatchedHandler(dispatcher).HandleAsync(
            new ShipmentDispatched
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = order,
                OccurredAt = DateTimeOffset.UtcNow,
                OrderId = order,
                TrackingNumber = "TRACK-1"
            },
            CancellationToken.None);

        FulfilReservationCommand command =
            dispatcher.Commands.OfType<FulfilReservationCommand>().ShouldHaveSingleItem();
        command.OrderId.ShouldBe(order);
    }

    // Records what it is sent rather than a mock: this project references no
    // mocking package, and neither handler under test needs anything richer
    // than a spy over one call.
    private sealed class RecordingDispatcher : IDispatcher
    {
        public List<object> Commands { get; } = [];

        public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(default(TResult)!);
        }

        public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default) =>
            throw new NotSupportedException("neither handler under test queries anything");
    }
}
