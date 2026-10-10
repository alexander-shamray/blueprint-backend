using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Common.Application;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Privacy.Api;
using Privacy.Infrastructure.Observability;
using Privacy.Infrastructure.Sweep;
using Privacy.TestSupport;
using Shouldly;
using Xunit;

namespace Privacy.Api.Tests;

/// <summary>Silence made visible and answerable: the sweep, the reissue and the gauges that name who is missing.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ErasureSweepAndReissueTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Route = "/v1/privacy/erasure-requests";

    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private OverdueSweepService Sweep => fixture.Factory.Services.GetRequiredService<OverdueSweepService>();

    [Fact]
    public async Task A_request_past_its_due_time_is_marked_overdue_and_keeps_the_subject_for_a_reissue()
    {
        Guid request = await RaiseAsync();
        await MakeDueAsync(request);

        (await Sweep.RunOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        (await StatusAsync(request)).ShouldBe("Overdue");
        (await fixture.ScalarAsync<Guid>(
            "SELECT Value = SubjectId FROM privacy.ErasureRequests WHERE RequestId = {0}",
            request)).ShouldBe(Subject);
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM privacy.ErasureRequests WHERE RequestId = {0} AND OverdueAt IS NOT NULL",
            request)).ShouldBe(1);
    }

    [Fact]
    public async Task A_second_pass_finds_nothing_more_and_a_request_not_yet_due_is_left_open()
    {
        Guid due = await RaiseAsync();
        await MakeDueAsync(due);
        Guid notDue = await RaiseAsync(Guid.CreateVersion7());

        await Sweep.RunOnceAsync(TestContext.Current.CancellationToken);

        (await Sweep.RunOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await StatusAsync(notDue)).ShouldBe("Open");
    }

    [Fact]
    public async Task A_closed_request_is_never_marked_overdue()
    {
        Guid request = await RaiseAsync();
        await fixture.ExecuteAsync(
            "UPDATE privacy.ErasureRequests SET Status = 'Closed', SubjectId = NULL, DueAt = DATEADD(day, -1, SYSDATETIMEOFFSET()) " +
            "WHERE RequestId = {0}",
            request);

        (await Sweep.RunOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await StatusAsync(request)).ShouldBe("Closed");
    }

    [Fact]
    public async Task Reissuing_an_overdue_request_reopens_it_and_broadcasts_it_again_under_a_fresh_message()
    {
        Guid request = await RaiseAsync();
        await MakeDueAsync(request);
        await Sweep.RunOnceAsync(TestContext.Current.CancellationToken);

        HttpResponseMessage response = await ReissueAsync(request, Guid.CreateVersion7(), PrivacyPermissions.Erase);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StatusAsync(request)).ShouldBe("Open");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = Reissues FROM privacy.ErasureRequests WHERE RequestId = {0}",
            request)).ShouldBe(1);

        IReadOnlyList<OutboxMessage> broadcasts = [.. (await fixture.OutboxAsync()).OrderBy(m => m.Id)];
        broadcasts.Count.ShouldBe(2);
        broadcasts.Select(m => m.CorrelationId).Distinct().ShouldBe([request], "the same request");
        broadcasts[1].MessageId.ShouldNotBe(broadcasts[0].MessageId, "under a fresh message (ADR-092)");
    }

    [Fact]
    public async Task The_same_reissue_sent_twice_broadcasts_once()
    {
        Guid request = await RaiseAsync();
        Guid key = Guid.CreateVersion7();
        Guid operator_ = Guid.CreateVersion7();

        // One caller, since the key's first segment is the caller's id (§8.5).
        HttpResponseMessage first = await ReissueAsync(request, key, PrivacyPermissions.Erase, operator_);
        HttpResponseMessage second = await ReissueAsync(request, key, PrivacyPermissions.Erase, operator_);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await fixture.OutboxAsync()).Count.ShouldBe(2, "the raise and one reissue, not two");
    }

    [Fact]
    public async Task A_closed_request_cannot_be_reissued_and_an_unknown_one_is_a_404()
    {
        Guid request = await RaiseAsync();
        await fixture.ExecuteAsync(
            "UPDATE privacy.ErasureRequests SET Status = 'Closed', SubjectId = NULL WHERE RequestId = {0}",
            request);

        (await ReissueAsync(request, Guid.CreateVersion7(), PrivacyPermissions.Erase))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReissueAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), PrivacyPermissions.Erase))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await fixture.OutboxAsync()).ShouldHaveSingleItem("only the raise");
    }

    [Fact]
    public async Task A_reissue_needs_the_permission_and_a_command_id()
    {
        Guid request = await RaiseAsync();

        (await ReissueAsync(request, Guid.CreateVersion7(), permission: null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReissueAsync(request, Guid.Empty, PrivacyPermissions.Erase))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_gauges_count_overdue_requests_and_name_the_holders_that_have_not_answered()
    {
        Guid request = await RaiseAsync();
        await fixture.ExecuteAsync(
            "INSERT INTO privacy.ErasureCompletions (RequestId, Responder, Count, Counted, ReceivedAt) " +
            "VALUES ({0}, 'ordering', 1, 1, SYSDATETIMEOFFSET()), ({0}, 'catalog', 1, 0, SYSDATETIMEOFFSET())",
            request);
        await MakeDueAsync(request);
        await Sweep.RunOnceAsync(TestContext.Current.CancellationToken);

        IErasureStats stats = fixture.Factory.Services.GetRequiredService<IErasureStats>();

        stats.OverdueCount().ShouldBe(1);
        stats.OverdueMissingByResponder().ShouldBe(
            new Dictionary<string, int> { ["payments"] = 1, ["shipping"] = 1, ["notifications"] = 1, ["bff"] = 1 },
            ignoreOrder: true);
    }

    [Fact]
    public void The_gauges_report_zero_when_nothing_is_overdue_and_name_no_holder()
    {
        (IReadOnlyList<(string Name, int Value, string? Responder)> seen, _) = Observe(new FixedStats(0, new Dictionary<string, int>()));

        seen.ShouldBe([("privacy.erasure.overdue", 0, null)], "none overdue reads as a value, not as no data");
    }

    [Fact]
    public void The_gauges_report_the_total_and_each_holder_missing_from_something()
    {
        (IReadOnlyList<(string Name, int Value, string? Responder)> seen, _) = Observe(
            new FixedStats(3, new Dictionary<string, int> { ["payments"] = 3, ["bff"] = 1 }));

        seen.ShouldBe(
            [
                ("privacy.erasure.overdue", 3, null),
                ("privacy.erasure.overdue.missing", 3, "payments"),
                ("privacy.erasure.overdue.missing", 1, "bff")
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void A_failing_read_yields_no_measurements_rather_than_throwing()
    {
        (IReadOnlyList<(string Name, int Value, string? Responder)> seen, Exception? raised) = Observe(new BrokenStats());

        raised.ShouldBeNull("the collector abandons its pass on an exception, so the gauge must contain it");
        seen.ShouldBeEmpty("absent rather than wrong (§13.6)");
    }

    private static (IReadOnlyList<(string Name, int Value, string? Responder)> Seen, Exception? Raised) Observe(
        IErasureStats stats)
    {
        using IMeterFactory factory = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();

        ErasureMetrics metrics = new(factory, stats, NullLogger<ErasureMetrics>.Instance);
        metrics.ShouldNotBeNull();

        // Filtered on the meter instance, never its name: a MeterListener is process-wide.
        Meter mine = factory.Create(OutboxMetrics.MeterName);
        List<(string Name, int Value, string? Responder)> seen = [];

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, mine) && instrument.Name.StartsWith("privacy.", StringComparison.Ordinal))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>(
            (instrument, value, tags, _) => seen.Add((instrument.Name, value, ResponderOf(tags))));
        listener.Start();

        Exception? raised = Record.Exception(() => listener.RecordObservableInstruments());

        return (seen, raised);
    }

    private static string? ResponderOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == "responder")
                return tag.Value?.ToString();
        }

        return null;
    }

    private async Task<Guid> RaiseAsync(Guid? subject = null)
    {
        using HttpClient client = fixture.Factory.CreateClient();
        using HttpRequestMessage message = new(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new { subjectId = subject ?? Subject })
        };
        Authenticate(message, PrivacyPermissions.Erase);

        HttpResponseMessage response = await client.SendAsync(message, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> ReissueAsync(
        Guid request,
        Guid commandId,
        string? permission,
        Guid? caller = null)
    {
        using HttpClient client = fixture.Factory.CreateClient();
        using HttpRequestMessage message = new(HttpMethod.Post, $"{Route}/{request}/reissue")
        {
            Content = JsonContent.Create(new { commandId })
        };
        Authenticate(message, permission, caller);

        return await client.SendAsync(message, TestContext.Current.CancellationToken);
    }

    private static void Authenticate(HttpRequestMessage message, string? permission, Guid? caller = null)
    {
        message.Headers.Add(TestAuthHandler.UserHeader, (caller ?? Guid.CreateVersion7()).ToString());

        if (permission is not null)
            message.Headers.Add(TestAuthHandler.PermissionsHeader, permission);
    }

    private Task MakeDueAsync(Guid request) =>
        fixture.ExecuteAsync(
            "UPDATE privacy.ErasureRequests SET DueAt = DATEADD(minute, -5, SYSDATETIMEOFFSET()) WHERE RequestId = {0}",
            request);

    private Task<string> StatusAsync(Guid request) =>
        fixture.ScalarAsync<string>("SELECT Value = Status FROM privacy.ErasureRequests WHERE RequestId = {0}", request);

    private sealed class FixedStats(int overdue, IReadOnlyDictionary<string, int> missing) : IErasureStats
    {
        public int OverdueCount() => overdue;

        public IReadOnlyDictionary<string, int> OverdueMissingByResponder() => missing;
    }

    private sealed class BrokenStats : IErasureStats
    {
        public int OverdueCount() => throw new InvalidOperationException("database unreachable");

        public IReadOnlyDictionary<string, int> OverdueMissingByResponder() =>
            throw new InvalidOperationException("database unreachable");
    }
}
