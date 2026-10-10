using Common.Application;
using Common.Domain;
using Shipping.Application.Privacy;
using Shipping.Application.Privacy.ErasePersonalData;
using Shouldly;
using Xunit;

namespace Shipping.Application.Tests;

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
    public async Task The_subjects_addresses_are_deleted_and_the_audit_row_counts_them()
    {
        _store.Addresses = 2;

        Result<int> result = await Handler().HandleAsync(Command, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(2);
        _store.Subjects.ShouldBe([Subject]);
        PersonalDataErasure row = _erasures.Added.ShouldHaveSingleItem();
        row.Id.ShouldBe(Request);
        row.Count.ShouldBe(2);
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

    private sealed class FakeStore : IShippingPersonalDataStore
    {
        public int Addresses { get; set; }

        public List<Guid> Subjects { get; } = [];

        public Task<IReadOnlyList<Guid>> AddressedOrdersAsync(Guid subjectId, CancellationToken ct) =>
            throw new NotSupportedException("Reading the orders is the integration handler's step.");

        public Task<int> DeleteAddressesAsync(Guid subjectId, CancellationToken ct)
        {
            Subjects.Add(subjectId);
            return Task.FromResult(Addresses);
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
