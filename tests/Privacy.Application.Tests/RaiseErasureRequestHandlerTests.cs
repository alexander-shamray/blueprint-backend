using Common.Application;
using Microsoft.Extensions.Options;
using Privacy.Application.ErasureRequests;
using Privacy.Application.ErasureRequests.RaiseErasureRequest;
using Privacy.Domain.ErasureRequests;
using Shouldly;
using Xunit;

namespace Privacy.Application.Tests;

public class RaiseErasureRequestHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly TimeSpan Slo = TimeSpan.FromDays(30);

    private readonly FakeRequests _requests = new();

    private readonly PrivacyOptions _options = new()
    {
        Responders = ["ordering", "payments", "shipping", "notifications", "bff"],
        CompletionSlo = Slo
    };

    private RaiseErasureRequestHandler Handler() =>
        new(_requests, Options.Create(_options), new FixedClock(Now));

    [Fact]
    public async Task A_subject_with_no_request_gets_one_open_for_the_configured_holders_and_time()
    {
        Result<Guid> result = await Handler().HandleAsync(
            new RaiseErasureRequestCommand(Subject),
            TestContext.Current.CancellationToken);

        ErasureRequest added = _requests.Added.ShouldHaveSingleItem();
        result.Value.ShouldBe(added.Id);
        added.SubjectId.ShouldBe(Subject);
        added.Status.ShouldBe(ErasureStatus.Open);
        added.Responders.ShouldBe(_options.Responders);
        added.RaisedAt.ShouldBe(Now);
        added.DueAt.ShouldBe(Now + Slo);
    }

    [Fact]
    public async Task A_subject_with_a_request_still_open_gets_that_request_and_nothing_is_added()
    {
        ErasureRequest open = ErasureRequest.Raise(Guid.CreateVersion7(), Subject, ["ordering"], Slo, Now.AddDays(-1));
        _requests.Hold(open);

        Result<Guid> result = await Handler().HandleAsync(
            new RaiseErasureRequestCommand(Subject),
            TestContext.Current.CancellationToken);

        result.Value.ShouldBe(open.Id);
        _requests.Added.ShouldBeEmpty("a second request would be a second broadcast for one person");
    }

    [Fact]
    public async Task The_set_is_read_when_the_request_is_raised_and_stays_as_it_was_then()
    {
        await Handler().HandleAsync(new RaiseErasureRequestCommand(Subject), TestContext.Current.CancellationToken);

        _options.Responders = ["ordering"];

        _requests.Added.ShouldHaveSingleItem().Responders
            .ShouldBe(["ordering", "payments", "shipping", "notifications", "bff"]);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
