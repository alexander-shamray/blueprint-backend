using Common.Application;
using Notifications.Application.Privacy;
using Notifications.Application.Privacy.EndErasedSubject;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

public class EndErasedSubjectHandlerTests
{
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Every_kind_of_row_the_erasure_reaches_is_ended_in_its_order_and_no_audit_row_is_written()
    {
        RecordingStore store = new();

        Result result = await new EndErasedSubjectHandler(store)
            .HandleAsync(new EndErasedSubjectCommand(Subject), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        store.Calls.ShouldBe(
            ["waiting", "ended", "orders", "contact"],
            "a notice sent before the check keeps the customer's id until it is anonymised, and the notices are " +
            "found through the order records, so those go after them");
        store.Subjects.ShouldAllBe(s => s == Subject);
    }

    private sealed class RecordingStore : INotificationsPersonalDataStore
    {
        public List<string> Calls { get; } = [];

        public List<Guid> Subjects { get; } = [];

        public Task<int> DeleteWaitingNoticesAsync(Guid subjectId, CancellationToken ct) => Run("waiting", subjectId);

        public Task<int> AnonymiseEndedNoticesAsync(Guid subjectId, CancellationToken ct) => Run("ended", subjectId);

        public Task<int> DeleteOrderRecordsAsync(Guid subjectId, CancellationToken ct) => Run("orders", subjectId);

        public Task<int> DeleteContactAsync(Guid subjectId, CancellationToken ct) => Run("contact", subjectId);

        private Task<int> Run(string call, Guid subjectId)
        {
            Calls.Add(call);
            Subjects.Add(subjectId);

            return Task.FromResult(0);
        }
    }
}
