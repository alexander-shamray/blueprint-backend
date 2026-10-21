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

    private readonly FakeStore _store;
    private readonly FakeErasures _erasures;

    public ErasePersonalDataHandlerTests()
    {
        _store = new FakeStore();
        _erasures = new FakeErasures(_store);
    }

    private ErasePersonalDataHandler Handler() => new(_store, _erasures, new FixedClock(Now));

    [Fact]
    public async Task The_subject_is_locked_before_either_statement_runs()
    {
        await Handler().HandleAsync(Command, TestContext.Current.CancellationToken);

        _erasures.Locked.ShouldBe([(Subject, 0)], "a placement holding the lock has to commit before they look");
    }

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
    public async Task A_reissued_request_finds_its_row_and_reports_the_larger_count()
    {
        PersonalDataErasure first = PersonalDataErasure.Record(Request, Subject, 5, Now.AddDays(-1));
        first.ClearDomainEvents();
        _erasures.Existing[Request] = first;

        Result<int> result = await Handler().HandleAsync(Command, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(5, "a reissue after a lost send reports what the first pass erased");
        _erasures.Added.ShouldBeEmpty("a second row for one request would break the key");
        first.Count.ShouldBe(5);
        first.ErasedAt.ShouldBe(Now);
    }

    private sealed class FakeStore : IOrderPersonalDataStore
    {
        public int Orders { get; set; }

        public int Summaries { get; set; }

        public List<Guid> Subjects { get; } = [];

        public List<Guid> Requests { get; } = [];

        public Task<int> AnonymiseOrdersAsync(Guid subjectId, Guid requestId, CancellationToken ct)
        {
            Subjects.Add(subjectId);
            Requests.Add(requestId);
            return Task.FromResult(Orders);
        }

        public Task<int> AnonymiseSummariesAsync(Guid subjectId, Guid requestId, CancellationToken ct)
        {
            Subjects.Add(subjectId);
            Requests.Add(requestId);
            return Task.FromResult(Summaries);
        }
    }

    private sealed class FakeErasures(FakeStore store) : IPersonalDataErasureRepository
    {
        public Dictionary<Guid, PersonalDataErasure> Existing { get; } = [];

        /// <summary>Each subject locked, with how many statements had already run when it was.</summary>
        public List<(Guid Subject, int StatementsBefore)> Locked { get; } = [];

        public List<PersonalDataErasure> Added { get; } = [];

        public Task<PersonalDataErasure?> GetAsync(Guid requestId, CancellationToken ct) =>
            Task.FromResult(Existing.GetValueOrDefault(requestId));

        public Task<bool> IsClosedAsync(Guid requestId, CancellationToken ct) => throw new NotSupportedException();

        public Task LockSubjectAsync(Guid subjectId, CancellationToken ct)
        {
            Locked.Add((subjectId, store.Subjects.Count));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Guid>> RequestsErasingAsync(
            Guid subjectId,
            DateTimeOffset sourcedAt,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public void Add(PersonalDataErasure erasure) => Added.Add(erasure);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
