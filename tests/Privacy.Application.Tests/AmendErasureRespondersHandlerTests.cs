using Common.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Privacy.Application.ErasureRequests;
using Privacy.Application.ErasureRequests.AmendResponders;
using Privacy.Domain.ErasureRequests;
using Shouldly;
using Xunit;

namespace Privacy.Application.Tests;

public class AmendErasureRespondersHandlerTests
{
    private static readonly DateTimeOffset Raised = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Operator = new("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private readonly FakeRequests _requests = new();

    private AmendErasureRespondersHandler Handler(DateTimeOffset now) =>
        new(_requests, new Staff(Operator), new FixedClock(now), NullLogger<AmendErasureRespondersHandler>.Instance);

    private static AmendErasureRespondersCommand Command(Guid request, string[] add, string[] remove) =>
        new(Guid.CreateVersion7(), request, add, remove, "the set named a holder that does not exist");

    [Fact]
    public async Task The_amendment_is_made_under_the_callers_name_and_at_the_clocks_time()
    {
        ErasureRequest request =
            ErasureRequest.Raise(Request, Subject, ["ordering", "ghost"], TimeSpan.FromDays(30), Raised);
        request.RecordCompletion("ordering", 1, Raised);
        _requests.Hold(request);
        DateTimeOffset now = Raised.AddDays(31);

        Result result = await Handler(now).HandleAsync(
            Command(Request, [], ["ghost"]),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        request.Status.ShouldBe(ErasureStatus.Closed);
        ErasureAmendment amendment = request.Amendments.ShouldHaveSingleItem();
        amendment.AmendedBy.ShouldBe(Operator);
        amendment.AmendedAt.ShouldBe(now);
    }

    [Fact]
    public async Task A_change_the_set_cannot_take_is_a_rule_failure_carrying_the_aggregates_reason()
    {
        ErasureRequest request = ErasureRequest.Raise(Request, Subject, ["ordering"], TimeSpan.FromDays(30), Raised);
        _requests.Hold(request);

        Result result = await Handler(Raised).HandleAsync(
            Command(Request, ["ordering"], []),
            TestContext.Current.CancellationToken);

        result.Error.Code.ShouldBe("erasure_request.amendment_refused");
        result.Error.Description.ShouldContain("already in the set");
        request.Amendments.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_closed_request_and_an_unknown_one_are_refused_with_their_own_errors()
    {
        ErasureRequest request = ErasureRequest.Raise(Request, Subject, ["ordering"], TimeSpan.FromDays(30), Raised);
        request.RecordCompletion("ordering", 1, Raised);
        _requests.Hold(request);

        (await Handler(Raised).HandleAsync(Command(Request, ["bff"], []), TestContext.Current.CancellationToken))
            .Error.ShouldBe(ErasureRequestErrors.Closed);
        (await Handler(Raised).HandleAsync(
            Command(Guid.CreateVersion7(), ["bff"], []),
            TestContext.Current.CancellationToken)).Error.ShouldBe(ErasureRequestErrors.NotFound);
    }

    private sealed class Staff(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid Id => id;

        public bool HasPermission(string permission) => true;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
