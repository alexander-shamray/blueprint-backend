using Common.Application;
using Common.Domain;
using Payments.Application.Orders;
using Payments.Application.Privacy;
using Payments.Application.Privacy.ErasePersonalData;
using Payments.Domain.Orders;
using Shouldly;
using Xunit;

namespace Payments.Application.Tests;

public class ErasePersonalDataHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly ErasePersonalDataCommand Command = new(Request, Subject);

    private readonly FakeOrders _orders = new();
    private readonly FakeErasures _erasures = new();

    private ErasePersonalDataHandler Handler() => new(_orders, _erasures, new FixedClock(Now));

    [Fact]
    public async Task The_subjects_orders_are_anonymised_and_the_audit_row_counts_them()
    {
        _orders.Anonymised = 2;

        Result<int> result = await Handler().HandleAsync(Command, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(2);
        _orders.Subjects.ShouldBe([Subject]);
        PersonalDataErasure row = _erasures.Added.ShouldHaveSingleItem();
        row.Id.ShouldBe(Request);
        row.Count.ShouldBe(2);
        row.SubjectHash.ShouldBe(PersonalDataErasure.HashSubject(Request, Subject));
    }

    [Fact]
    public async Task A_subject_with_nothing_here_is_still_recorded()
    {
        _orders.Anonymised = 0;

        Result<int> result = await Handler().HandleAsync(Command, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(0);
        _erasures.Added.ShouldHaveSingleItem().Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_reissued_request_finds_its_row_and_reports_again()
    {
        PersonalDataErasure first = PersonalDataErasure.Record(Request, Subject, 2, Now.AddDays(-1));
        first.ClearDomainEvents();
        _erasures.Existing[Request] = first;
        _orders.Anonymised = 0;

        Result<int> result = await Handler().HandleAsync(Command, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(0, "the answer is this pass's count, the row keeps the larger");
        _erasures.Added.ShouldBeEmpty("a second row for one request would break the key");
        first.Count.ShouldBe(2, "the first pass's count survives a pass that finds nothing");
        first.ErasedAt.ShouldBe(Now);
    }

    private sealed class FakeOrders : IPaymentOrderStore
    {
        public int Anonymised { get; set; }

        public List<Guid> Subjects { get; } = [];

        public Task<int> AnonymiseCustomerAsync(Guid subjectId, CancellationToken ct)
        {
            Subjects.Add(subjectId);
            return Task.FromResult(Anonymised);
        }

        public Task RecordPlacedAsync(
            OrderId id,
            Guid customerId,
            decimal total,
            string currency,
            DateTimeOffset placedAt,
            CancellationToken ct) =>
            throw new NotSupportedException("Not exercised on the erasure path.");

        public Task RecordCancelledAsync(OrderId id, DateTimeOffset cancelledAt, CancellationToken ct) =>
            throw new NotSupportedException("Not exercised on the erasure path.");

        public Task<PaymentOrderRecord?> LockAsync(OrderId id, CancellationToken ct) =>
            throw new NotSupportedException("Not exercised on the erasure path.");
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
