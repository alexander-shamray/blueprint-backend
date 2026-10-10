using Common.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Privacy.Application.ErasureRequests;
using Privacy.Application.ErasureRequests.MarkOverdue;
using Privacy.Domain.ErasureRequests;
using Shouldly;
using Xunit;

namespace Privacy.Application.Tests;

public class MarkErasureRequestOverdueHandlerTests
{
    private static readonly DateTimeOffset Raised = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly TimeSpan Slo = TimeSpan.FromDays(30);

    private readonly FakeRequests _requests = new();

    private MarkErasureRequestOverdueHandler Handler(DateTimeOffset now) =>
        new(_requests, new FixedClock(now), NullLogger<MarkErasureRequestOverdueHandler>.Instance);

    private ErasureRequest Held()
    {
        ErasureRequest request = ErasureRequest.Raise(Request, Guid.CreateVersion7(), ["ordering"], Slo, Raised);
        _requests.Hold(request);

        return request;
    }

    [Fact]
    public async Task A_request_past_its_due_time_becomes_overdue()
    {
        ErasureRequest request = Held();

        Result result = await Handler(Raised + Slo).HandleAsync(
            new MarkErasureRequestOverdueCommand(Request),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        request.Status.ShouldBe(ErasureStatus.Overdue);
        request.SubjectId.ShouldNotBeNull("an overdue request keeps the id, because a reissue needs it");
    }

    [Fact]
    public async Task A_request_not_yet_due_is_left_open_and_that_is_a_success()
    {
        ErasureRequest request = Held();

        Result result = await Handler(Raised + Slo - TimeSpan.FromMinutes(1)).HandleAsync(
            new MarkErasureRequestOverdueCommand(Request),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue("the sweep read it as due a moment ago; it may have been reissued since");
        request.Status.ShouldBe(ErasureStatus.Open);
    }

    [Fact]
    public async Task An_unknown_request_is_a_not_found()
    {
        Result result = await Handler(Raised).HandleAsync(
            new MarkErasureRequestOverdueCommand(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        result.Error.ShouldBe(ErasureRequestErrors.NotFound);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
