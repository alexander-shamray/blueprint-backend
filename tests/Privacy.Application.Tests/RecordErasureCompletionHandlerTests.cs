using Common.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Privacy.Application.ErasureRequests;
using Privacy.Application.ErasureRequests.RecordCompletion;
using Privacy.Domain.ErasureRequests;
using Shouldly;
using Xunit;

namespace Privacy.Application.Tests;

public class RecordErasureCompletionHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly FakeRequests _requests = new();

    private RecordErasureCompletionHandler Handler() =>
        new(_requests, new FixedClock(Now.AddHours(1)), NullLogger<RecordErasureCompletionHandler>.Instance);

    private ErasureRequest Held(params string[] responders)
    {
        ErasureRequest request = ErasureRequest.Raise(Request, Subject, responders, TimeSpan.FromDays(30), Now);
        _requests.Hold(request);

        return request;
    }

    [Fact]
    public async Task An_expected_holders_answer_is_counted_at_the_handlers_clock()
    {
        ErasureRequest request = Held("ordering", "payments");

        Result result = await Handler().HandleAsync(
            new RecordErasureCompletionCommand(Request, "ordering", 3),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        request.Missing.ShouldBe(["payments"]);
        request.Completions.Single().ReceivedAt.ShouldBe(Now.AddHours(1));
        request.Status.ShouldBe(ErasureStatus.Open);
    }

    [Fact]
    public async Task The_last_expected_answer_closes_the_request()
    {
        ErasureRequest request = Held("ordering");

        await Handler().HandleAsync(
            new RecordErasureCompletionCommand(Request, "ordering", 0),
            TestContext.Current.CancellationToken);

        request.Status.ShouldBe(ErasureStatus.Closed);
        request.SubjectId.ShouldBeNull();
    }

    [Fact]
    public async Task A_name_outside_the_set_is_a_success_that_flags_it_and_closes_nothing()
    {
        ErasureRequest request = Held("ordering");

        Result result = await Handler().HandleAsync(
            new RecordErasureCompletionCommand(Request, "catalog", 1),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue("a refusal would only be acked and counted, and the answer is worth keeping");
        request.Status.ShouldBe(ErasureStatus.Open);
        request.Completions.Single().Counted.ShouldBeFalse();
    }

    [Fact]
    public async Task An_answer_for_a_request_nobody_raised_is_a_not_found_the_consumer_acks()
    {
        Result result = await Handler().HandleAsync(
            new RecordErasureCompletionCommand(Guid.CreateVersion7(), "ordering", 1),
            TestContext.Current.CancellationToken);

        result.Error.ShouldBe(ErasureRequestErrors.NotFound);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
