using Common.Application;
using Common.Domain;
using Ordering.Application.Privacy;
using Ordering.Application.Privacy.ErasePersonalData;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

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
    public async Task Orders_and_summaries_are_both_anonymised_and_the_audit_row_counts_them()
    {
        _store.Orders = 2;
        _store.Summaries = 3;

        Result<int> result = await Handler().HandleAsync(Command, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(5);
        _store.Subjects.ShouldBe([Subject, Subject]);
        PersonalDataErasure row = _erasures.Added.ShouldHaveSingleItem();
        row.Id.ShouldBe(Request);
        row.Count.ShouldBe(5);
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

    private sealed class FakeStore : IOrderPersonalDataStore
    {
        public int Orders { get; set; }

        public int Summaries { get; set; }

        public List<Guid> Subjects { get; } = [];

        public Task<int> AnonymiseOrdersAsync(Guid subjectId, CancellationToken ct)
        {
            Subjects.Add(subjectId);
            return Task.FromResult(Orders);
        }

        public Task<int> AnonymiseSummariesAsync(Guid subjectId, CancellationToken ct)
        {
            Subjects.Add(subjectId);
            return Task.FromResult(Summaries);
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
