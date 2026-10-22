using Common.Application;
using Notifications.Application.Privacy;
using Notifications.Application.Privacy.EndErasedOrder;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

public class EndErasedOrderHandlerTests
{
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Order = new("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task Only_the_one_orders_rows_and_the_contact_are_ended_and_no_audit_row_is_written()
    {
        RecordingStore store = new();

        Result result = await new EndErasedOrderHandler(store)
            .HandleAsync(new EndErasedOrderCommand(Subject, Order), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        store.Calls.ShouldBe(
            ["waiting", "ended", "waiting deliveries", "ended deliveries", "record", "contact"],
            "the subject may have placed other orders after the erasure, and ADR-099 keeps them");
        store.Orders.ShouldAllBe(o => o == Order);
        store.Subjects.ShouldAllBe(s => s == Subject);
    }

    private sealed class RecordingStore : INotificationsPersonalDataStore
    {
        public List<string> Calls { get; } = [];

        public List<Guid> Orders { get; } = [];

        public List<Guid> Subjects { get; } = [];

        public Task<int> DeleteWaitingOrderNoticesAsync(Guid subjectId, Guid orderId, CancellationToken ct) =>
            Run("waiting", orderId);

        public Task<int> AnonymiseEndedOrderNoticesAsync(Guid orderId, CancellationToken ct) => Run("ended", orderId);

        public Task<int> DeleteWaitingOrderDeliveriesAsync(Guid orderId, CancellationToken ct) =>
            Run("waiting deliveries", orderId);

        public Task<int> AnonymiseEndedOrderDeliveriesAsync(Guid orderId, CancellationToken ct) =>
            Run("ended deliveries", orderId);

        public Task<int> DeleteOrderRecordAsync(Guid subjectId, Guid orderId, CancellationToken ct)
        {
            Subjects.Add(subjectId);
            return Run("record", orderId);
        }

        public Task<int> DeleteContactAsync(Guid subjectId, CancellationToken ct)
        {
            Calls.Add("contact");
            Subjects.Add(subjectId);

            return Task.FromResult(0);
        }

        public Task<int> DeleteWaitingNoticesAsync(Guid subjectId, Guid requestId, CancellationToken ct) => Refuse();

        public Task<int> AnonymiseEndedNoticesAsync(Guid subjectId, Guid requestId, CancellationToken ct) => Refuse();

        public Task<int> DeleteWaitingDeliveriesAsync(Guid subjectId, Guid requestId, CancellationToken ct) => Refuse();

        public Task<int> AnonymiseEndedDeliveriesAsync(Guid subjectId, Guid requestId, CancellationToken ct) =>
            Refuse();

        public Task<int> DeleteOrderRecordsAsync(Guid subjectId, Guid requestId, CancellationToken ct) => Refuse();

        public Task<int> ClearDevicesAsync(Guid subjectId, Guid requestId, CancellationToken ct) => Refuse();

        public Task<int> AnonymiseConsentAsync(
            Guid subjectId,
            Guid requestId,
            DateTimeOffset now,
            CancellationToken ct) =>
            Refuse();

        private Task<int> Run(string call, Guid orderId)
        {
            Calls.Add(call);
            Orders.Add(orderId);

            return Task.FromResult(0);
        }

        // A subject-wide statement would take the orders the subject placed after the erasure too (ADR-099).
        private static Task<int> Refuse() => throw new NotSupportedException();
    }
}
