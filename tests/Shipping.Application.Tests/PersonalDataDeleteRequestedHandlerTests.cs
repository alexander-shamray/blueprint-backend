using Common.Application;
using Common.Contracts.Privacy.V1;
using Shipping.Application.Privacy;
using Shipping.Application.Privacy.EndWaitingShipment;
using Shipping.Application.Privacy.ErasePersonalData;
using Shouldly;
using Xunit;

namespace Shipping.Application.Tests;

public class PersonalDataDeleteRequestedHandlerTests
{
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly PersonalDataDeleteRequested Requested = new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = Request,
        OccurredAt = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero),
        RequestId = Request,
        SubjectId = Subject
    };

    private readonly FakeDispatcher _dispatcher = new();
    private readonly FakeStore _store = new();
    private readonly FakeReporter _reporter = new();

    private PersonalDataDeleteRequestedHandler Handler() => new(_dispatcher, _store, _reporter);

    [Fact]
    public async Task Each_waiting_shipment_is_ended_before_the_addresses_go_and_the_count_is_reported()
    {
        Guid first = Guid.CreateVersion7();
        Guid second = Guid.CreateVersion7();
        _store.Orders = [first, second];
        _dispatcher.Count = 2;

        await Handler().HandleAsync(Requested, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldBe(
            [
                new EndWaitingShipmentCommand(first),
                new EndWaitingShipmentCommand(second),
                new ErasePersonalDataCommand(Request, Subject)
            ],
            "the rows that name the orders must outlive the step that reads them");
        _reporter.Reported.ShouldBe([(Request, 2)]);
    }

    [Fact]
    public async Task A_count_of_zero_is_reported_because_silence_cannot_be_told_from_success()
    {
        _dispatcher.Count = 0;

        await Handler().HandleAsync(Requested, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(new ErasePersonalDataCommand(Request, Subject));
        _reporter.Reported.ShouldBe([(Request, 0)]);
    }

    [Fact]
    public async Task A_failed_erasure_reports_nothing()
    {
        _dispatcher.Fault = new InvalidOperationException("the unit rolled back");

        await Should.ThrowAsync<InvalidOperationException>(
            () => Handler().HandleAsync(Requested, TestContext.Current.CancellationToken));

        _reporter.Reported.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_result_is_thrown_with_its_error_and_reports_nothing()
    {
        _dispatcher.Failure = new Error("erasure.refused", "No.", ErrorType.Rule);

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => Handler().HandleAsync(Requested, TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("erasure.refused");
        _reporter.Reported.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_shipment_that_cannot_be_ended_stops_the_erasure_and_reports_nothing()
    {
        _store.Orders = [Guid.CreateVersion7()];
        _dispatcher.Failure = new Error("shipment.refused", "No.", ErrorType.Rule);

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => Handler().HandleAsync(Requested, TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("shipment.refused");
        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBeOfType<EndWaitingShipmentCommand>();
        _reporter.Reported.ShouldBeEmpty();
    }

    private sealed class FakeStore : IShippingPersonalDataStore
    {
        public IReadOnlyList<Guid> Orders { get; set; } = [];

        public Task<IReadOnlyList<Guid>> AddressedOrdersAsync(Guid subjectId, CancellationToken ct) =>
            Task.FromResult(Orders);

        public Task<int> DeleteAddressesAsync(Guid subjectId, CancellationToken ct) =>
            throw new NotSupportedException("The erasure is a command; the handler under test only dispatches it.");
    }

    private sealed class FakeDispatcher : IDispatcher
    {
        public int Count { get; set; }

        public Error? Failure { get; set; }

        public Exception? Fault { get; set; }

        public List<object> Sent { get; } = [];

        public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct)
        {
            if (Fault is not null)
                throw Fault;

            Sent.Add(command);

            object result = command switch
            {
                EndWaitingShipmentCommand => Failure is null ? Result.Success() : Result.Failure(Failure),
                _ => Failure is null ? Result.Success(Count) : Result.Failure<int>(Failure)
            };

            return Task.FromResult((TResult)result);
        }

        public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeReporter : IErasureReporter
    {
        public List<(Guid RequestId, int Count)> Reported { get; } = [];

        public Task ReportAsync(Guid requestId, int count, CancellationToken ct)
        {
            Reported.Add((requestId, count));
            return Task.CompletedTask;
        }
    }
}
