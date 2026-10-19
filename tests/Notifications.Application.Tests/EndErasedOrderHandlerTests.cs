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
    public async Task Only_the_one_orders_notices_and_record_are_ended_and_no_audit_row_is_written()
    {
        RecordingStore store = new();

        Result result = await new EndErasedOrderHandler(store)
            .HandleAsync(new EndErasedOrderCommand(Subject, Order), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        store.Calls.ShouldBe(
            ["waiting", "ended", "record"],
            "the subject may have placed other orders after the erasure, and the notices are found through the " +
            "record, so it goes after them");
        store.Orders.ShouldAllBe(o => o == Order);
        store.Subjects.ShouldHaveSingleItem().ShouldBe(Subject);
    }

    private sealed class RecordingStore : INotificationsPersonalDataStore
    {
        public List<string> Calls { get; } = [];

        public List<Guid> Orders { get; } = [];

        public List<Guid> Subjects { get; } = [];

        public Task<int> DeleteWaitingOrderNoticesAsync(Guid orderId, CancellationToken ct) => Run("waiting", orderId);

        public Task<int> AnonymiseEndedOrderNoticesAsync(Guid orderId, CancellationToken ct) => Run("ended", orderId);

        public Task<int> DeleteOrderRecordAsync(Guid subjectId, Guid orderId, CancellationToken ct)
        {
            Subjects.Add(subjectId);
            return Run("record", orderId);
        }

        public Task<int> DeleteWaitingNoticesAsync(Guid subjectId, CancellationToken ct) => Refuse();

        public Task<int> AnonymiseEndedNoticesAsync(Guid subjectId, CancellationToken ct) => Refuse();

        public Task<int> DeleteWaitingDeliveriesAsync(Guid subjectId, CancellationToken ct) => Refuse();

        public Task<int> AnonymiseEndedDeliveriesAsync(Guid subjectId, CancellationToken ct) => Refuse();

        public Task<int> DeleteOrderRecordsAsync(Guid subjectId, CancellationToken ct) => Refuse();

        public Task<int> DeleteContactAsync(Guid subjectId, CancellationToken ct) => Refuse();

        public Task<int> ClearDevicesAsync(Guid subjectId, CancellationToken ct) => Refuse();

        public Task<int> AnonymiseConsentAsync(Guid subjectId, DateTimeOffset now, CancellationToken ct) => Refuse();

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
