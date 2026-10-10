using Common.Application;
using Microsoft.Extensions.Options;
using Privacy.Application.ErasureRequests;
using Privacy.Application.ErasureRequests.ReissueErasureRequest;
using Privacy.Domain.ErasureRequests;
using Privacy.Domain.ErasureRequests.Events;
using Shouldly;
using Xunit;

namespace Privacy.Application.Tests;

public class ReissueErasureRequestHandlerTests
{
    private static readonly DateTimeOffset Raised = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly TimeSpan Slo = TimeSpan.FromDays(30);

    private readonly FakeRequests _requests = new();

    private ReissueErasureRequestHandler Handler(DateTimeOffset now) =>
        new(_requests, Options.Create(new PrivacyOptions { Responders = ["ordering"], CompletionSlo = Slo }), new FixedClock(now));

    private static ReissueErasureRequestCommand Command(Guid request) => new(Guid.CreateVersion7(), request);

    [Fact]
    public async Task An_overdue_request_is_reopened_with_a_fresh_time_and_broadcast_again()
    {
        ErasureRequest request = ErasureRequest.Raise(Request, Subject, ["ordering"], Slo, Raised);
        request.MarkOverdue(Raised + Slo);
        request.ClearDomainEvents();
        _requests.Hold(request);
        DateTimeOffset now = Raised + Slo + TimeSpan.FromDays(3);

        Result result = await Handler(now).HandleAsync(Command(Request), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        request.Status.ShouldBe(ErasureStatus.Open);
        request.DueAt.ShouldBe(now + Slo, "the configured service level, read now and not when it was raised");
        request.DomainEvents.ShouldHaveSingleItem().ShouldBe(new ErasureRequestedDomainEvent(Request, Subject, now));
    }

    [Fact]
    public async Task A_closed_request_is_refused_with_its_own_error()
    {
        ErasureRequest request = ErasureRequest.Raise(Request, Subject, ["ordering"], Slo, Raised);
        request.RecordCompletion("ordering", 1, Raised);
        _requests.Hold(request);

        Result result = await Handler(Raised.AddDays(1)).HandleAsync(
            Command(Request),
            TestContext.Current.CancellationToken);

        result.Error.ShouldBe(ErasureRequestErrors.Closed);
    }

    [Fact]
    public async Task An_unknown_request_is_a_not_found()
    {
        Result result = await Handler(Raised).HandleAsync(
            Command(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        result.Error.ShouldBe(ErasureRequestErrors.NotFound);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
