using Common.Application;
using Common.Contracts.Privacy.V1;
using Notifications.Application.Privacy;
using Notifications.Application.Privacy.ErasePersonalData;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

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
    private readonly FakeReporter _reporter = new();
    private readonly FakeErasures _erasures = new();

    private PersonalDataDeleteRequestedHandler Handler() => new(_dispatcher, _erasures, _reporter);

    [Fact]
    public async Task A_late_copy_of_a_closed_request_erases_and_reports_nothing()
    {
        _erasures.Closed = true;

        await Handler().HandleAsync(Requested, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldBeEmpty("a closed row is no longer asked, so a second pass is uncovered (ADR-139)");
        _reporter.Reported.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_erasure_is_dispatched_and_its_count_is_reported()
    {
        _dispatcher.Count = 3;

        await Handler().HandleAsync(Requested, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(new ErasePersonalDataCommand(Request, Subject));
        _reporter.Reported.ShouldBe([(Request, 3)]);
    }

    [Fact]
    public async Task A_count_of_zero_is_reported_because_silence_cannot_be_told_from_success()
    {
        _dispatcher.Count = 0;

        await Handler().HandleAsync(Requested, TestContext.Current.CancellationToken);

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
            return Task.FromResult((TResult)(object)(Failure is null
                ? Result.Success(Count)
                : Result.Failure<int>(Failure)));
        }

        public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeErasures : IPersonalDataErasureRepository
    {
        public bool Closed { get; set; }

        public Task<Common.Domain.PersonalDataErasure?> GetAsync(Guid requestId, CancellationToken ct) =>
            throw new NotSupportedException("The erasure is a command; the handler under test only dispatches it.");

        public Task<bool> IsClosedAsync(Guid requestId, CancellationToken ct) => Task.FromResult(Closed);

        public Task<IReadOnlyList<Guid>> RequestsErasingAsync(
            Guid subjectId,
            DateTimeOffset sourcedAt,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public void Add(Common.Domain.PersonalDataErasure erasure) => throw new NotSupportedException();
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
