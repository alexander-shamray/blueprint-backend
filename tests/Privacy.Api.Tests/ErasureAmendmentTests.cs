using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Domain;
using Privacy.Api;
using Privacy.Application.ErasureRequests.GetErasureRequest;
using Privacy.TestSupport;
using Shouldly;
using Xunit;

namespace Privacy.Api.Tests;

/// <summary>ADR-136's amendment through the real host: the set changed under the caller's name, and refusals.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ErasureAmendmentTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Route = "/v1/privacy/erasure-requests";
    private const string Reason = "privacy.responders named ghost, which no holder answers under";

    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Removing_a_name_no_holder_answers_under_closes_the_request_and_records_who_did_it()
    {
        Guid request = await RaiseUnderAsync("ordering,ghost", answered: "ordering");
        Guid operator_ = Guid.CreateVersion7();

        HttpResponseMessage response = await AmendAsync(request, [], ["ghost"], Reason, caller: operator_);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM privacy.ErasureRequests WHERE RequestId = {0}",
            request)).ShouldBe("Closed");
        (await fixture.ScalarAsync<string>(
            "SELECT Value = SubjectHash FROM privacy.ErasureRequests WHERE RequestId = {0}",
            request)).ShouldBe(PersonalDataErasure.HashSubject(request, Subject), "the id gave way to its hash");

        ErasureRequestView view = await ViewAsync(request);
        view.Responders.ShouldBe(["ordering"]);
        ResponderAmendment amendment = view.Amendments.ShouldHaveSingleItem();
        amendment.Removed.ShouldBe(["ghost"]);
        amendment.Added.ShouldBeEmpty();
        amendment.Reason.ShouldBe(Reason);
        amendment.AmendedBy.ShouldBe(operator_);
    }

    [Fact]
    public async Task Correcting_a_misspelt_name_counts_the_answer_already_heard_and_closes_the_request()
    {
        Guid request = await RaiseUnderAsync("ordering,shiping", answered: "ordering");
        await fixture.ExecuteAsync(
            """
            INSERT INTO privacy.ErasureCompletions (RequestId, Responder, Count, Counted, ReceivedAt)
            VALUES ({0}, 'shipping', 1, 0, SYSDATETIMEOFFSET())
            """,
            request);

        (await AmendAsync(request, ["shipping"], ["shiping"], Reason)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM privacy.ErasureRequests WHERE RequestId = {0}",
            request)).ShouldBe("Closed");
        (await fixture.ScalarAsync<bool>(
            """
            SELECT Value = Counted
            FROM privacy.ErasureCompletions
            WHERE RequestId = {0}
                AND Responder = 'shipping'
            """,
            request)).ShouldBeTrue("the recount is saved, not only held by the aggregate");
    }

    [Fact]
    public async Task An_amendment_publishes_nothing_so_a_holder_it_adds_waits_for_a_reissue()
    {
        Guid request = await RaiseUnderAsync("ordering", answered: null);
        int before = (await fixture.OutboxAsync()).Count;

        (await AmendAsync(request, ["bff"], [], Reason)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await fixture.OutboxAsync()).Count.ShouldBe(before);
        (await ViewAsync(request)).Missing.ShouldBe(["ordering", "bff"]);
    }

    [Fact]
    public async Task The_same_amendment_sent_twice_is_recorded_once()
    {
        Guid request = await RaiseUnderAsync("ordering", answered: null);
        Guid key = Guid.CreateVersion7();
        Guid operator_ = Guid.CreateVersion7();

        // One caller, since the key's first segment is the caller's id (§8.5).
        (await AmendAsync(request, ["bff"], [], Reason, key, operator_)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AmendAsync(request, ["bff"], [], Reason, key, operator_)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ViewAsync(request)).Amendments.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_change_the_set_cannot_take_is_refused_with_the_aggregates_reason()
    {
        Guid request = await RaiseUnderAsync("ordering", answered: null);

        HttpResponseMessage response = await AmendAsync(request, [], ["payments"], Reason);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("code").GetString().ShouldBe("erasure_request.amendment_refused");
        (await ViewAsync(request)).Amendments.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_closed_request_is_refused_and_an_unknown_one_is_a_404()
    {
        Guid request = await RaiseUnderAsync("ordering", answered: null);
        await fixture.ExecuteAsync(
            "UPDATE privacy.ErasureRequests SET Status = 'Closed', SubjectId = NULL WHERE RequestId = {0}",
            request);

        (await AmendAsync(request, ["bff"], [], Reason)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await AmendAsync(Guid.CreateVersion7(), ["bff"], [], Reason)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_amendment_needs_the_permission_a_command_id_and_a_reason()
    {
        Guid request = await RaiseUnderAsync("ordering", answered: null);

        (await AmendAsync(request, ["bff"], [], Reason, permission: null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AmendAsync(request, ["bff"], [], Reason, Guid.Empty))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AmendAsync(request, ["bff"], [], "")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AmendAsync(request, ["Not A Name"], [], Reason)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AmendAsync(request, [null!], [], Reason)).StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "a null name is refused here, not dereferenced by the aggregate (§6.4)");
    }

    /// <summary>Raised through the route, then given the set and the answer the case needs.</summary>
    private async Task<Guid> RaiseUnderAsync(string respondersCsv, string? answered)
    {
        using HttpClient client = fixture.Factory.CreateClient();
        using HttpRequestMessage message = new(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new { subjectId = Subject })
        };
        Authenticate(message, PrivacyPermissions.Erase);

        HttpResponseMessage response = await client.SendAsync(message, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid request = await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        await fixture.ExecuteAsync(
            "UPDATE privacy.ErasureRequests SET RespondersCsv = {0} WHERE RequestId = {1}",
            respondersCsv,
            request);

        if (answered is not null)
        {
            await fixture.ExecuteAsync(
                """
                INSERT INTO privacy.ErasureCompletions (RequestId, Responder, Count, Counted, ReceivedAt)
                VALUES ({0}, {1}, 1, 1, SYSDATETIMEOFFSET())
                """,
                request,
                answered);
        }

        return request;
    }

    private async Task<HttpResponseMessage> AmendAsync(
        Guid request,
        string[] add,
        string[] remove,
        string reason,
        Guid? commandId = null,
        Guid? caller = null,
        string? permission = PrivacyPermissions.Erase)
    {
        using HttpClient client = fixture.Factory.CreateClient();
        using HttpRequestMessage message = new(HttpMethod.Post, $"{Route}/{request}/responders")
        {
            Content = JsonContent.Create(new { commandId = commandId ?? Guid.CreateVersion7(), add, remove, reason })
        };
        Authenticate(message, permission, caller);

        return await client.SendAsync(message, TestContext.Current.CancellationToken);
    }

    private async Task<ErasureRequestView> ViewAsync(Guid request)
    {
        using HttpClient client = fixture.Factory.CreateClient();
        using HttpRequestMessage message = new(HttpMethod.Get, $"{Route}/{request}");
        Authenticate(message, PrivacyPermissions.Erase);

        HttpResponseMessage response = await client.SendAsync(message, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<ErasureRequestView>(TestContext.Current.CancellationToken))!;
    }

    private static void Authenticate(HttpRequestMessage message, string? permission, Guid? caller = null)
    {
        message.Headers.Add(TestAuthHandler.UserHeader, (caller ?? Guid.CreateVersion7()).ToString());

        if (permission is not null)
            message.Headers.Add(TestAuthHandler.PermissionsHeader, permission);
    }
}
