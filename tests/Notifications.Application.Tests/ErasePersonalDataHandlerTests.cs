using Common.Application;
using Common.Domain;
using Notifications.Application.Privacy;
using Notifications.Application.Privacy.ErasePersonalData;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

public class ErasePersonalDataHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly ErasePersonalDataCommand Command = new(Request, Subject);

    private readonly FakeStore _store = new();
    private readonly FakeErasures _erasures = new();

    private ErasePersonalDataHandler Handler() => new(_store, _erasures, new FixedClock(Now));

    [Fact]
    public async Task Every_kind_of_row_is_erased_in_order_and_the_audit_row_counts_them_all()
    {
        _store.Waiting = 1;
        _store.Ended = 2;
        _store.Orders = 3;
        _store.Contacts = 1;

        Result<int> result = await Handler().HandleAsync(Command, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(7);
        _store.Calls.ShouldBe(
            ["waiting", "ended", "orders", "contact"],
            "the notices are found through the order records, so those go after them");
        PersonalDataErasure row = _erasures.Added.ShouldHaveSingleItem();
        row.Id.ShouldBe(Request);
        row.Count.ShouldBe(7);
        row.SubjectHash.ShouldBe(PersonalDataErasure.HashSubject(Request, Subject));
    }

    [Fact]
    public async Task A_subject_with_nothing_here_is_still_recorded()
    {
        Result<int> result = await Handler().HandleAsync(Command, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(0);
        _erasures.Added.ShouldHaveSingleItem().Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_reissued_request_finds_its_row_and_keeps_the_larger_count()
    {
        PersonalDataErasure first = PersonalDataErasure.Record(Request, Subject, 5, Now.AddDays(-1));
        first.ClearDomainEvents();
        _erasures.Existing[Request] = first;

        Result<int> result = await Handler().HandleAsync(Command, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(0, "the answer is this pass's count, the row keeps the larger");
        _erasures.Added.ShouldBeEmpty("a second row for one request would break the key");
        first.Count.ShouldBe(5);
        first.ErasedAt.ShouldBe(Now);
    }

    private sealed class FakeStore : INotificationsPersonalDataStore
    {
        public int Waiting { get; set; }

        public int Ended { get; set; }

        public int Orders { get; set; }

        public int Contacts { get; set; }

        public List<string> Calls { get; } = [];

        public Task<int> DeleteWaitingNoticesAsync(Guid subjectId, CancellationToken ct) => Run("waiting", Waiting);

        public Task<int> AnonymiseEndedNoticesAsync(Guid subjectId, CancellationToken ct) => Run("ended", Ended);

        public Task<int> DeleteOrderRecordsAsync(Guid subjectId, CancellationToken ct) => Run("orders", Orders);

        public Task<int> DeleteContactAsync(Guid subjectId, CancellationToken ct) => Run("contact", Contacts);

        private Task<int> Run(string call, int rows)
        {
            Calls.Add(call);
            return Task.FromResult(rows);
        }
    }

    private sealed class FakeErasures : IPersonalDataErasureRepository
    {
        public Dictionary<Guid, PersonalDataErasure> Existing { get; } = [];

        public List<PersonalDataErasure> Added { get; } = [];

        public Task<PersonalDataErasure?> GetAsync(Guid requestId, CancellationToken ct) =>
            Task.FromResult(Existing.GetValueOrDefault(requestId));

        public void Add(PersonalDataErasure erasure) => Added.Add(erasure);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
