using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Application;
using Common.Contracts.Privacy.V1;
using Common.Infrastructure.Outbox;
using Privacy.Api;
using Privacy.TestSupport;
using Shouldly;
using Xunit;

namespace Privacy.Api.Tests;

/// <summary>ADR-092's raise and status routes over a real database, under the permission §11.4 names.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ErasureRequestEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Route = "/v1/privacy/erasure-requests";

    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Other = new("cccccccc-cccc-cccc-cccc-cccccccccccc");

    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Raising_a_request_opens_it_for_the_five_holders_and_stages_the_broadcast()
    {
        HttpResponseMessage response = await PostAsync(Subject, PrivacyPermissions.Erase);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid id = await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        (await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM privacy.ErasureRequests WHERE RequestId = {0}",
            id)).ShouldBe("Open");
        (await fixture.ScalarAsync<Guid>(
            "SELECT Value = SubjectId FROM privacy.ErasureRequests WHERE RequestId = {0}",
            id)).ShouldBe(Subject);
        (await fixture.ScalarAsync<string>(
            "SELECT Value = RespondersCsv FROM privacy.ErasureRequests WHERE RequestId = {0}",
            id)).ShouldBe("ordering,payments,shipping,notifications,bff");

        OutboxMessage staged = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        staged.Lane.ShouldBe(OutboxLane.Broker);
        staged.MessageType.ShouldContain(nameof(PersonalDataDeleteRequested));
        staged.CorrelationId.ShouldBe(id);
    }

    [Fact]
    public async Task Asking_again_for_a_subject_with_a_request_open_returns_it_and_publishes_nothing_more()
    {
        Guid first = await RaiseAsync(Subject);

        HttpResponseMessage again = await PostAsync(Subject, PrivacyPermissions.Erase);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken)).ShouldBe(first);
        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM privacy.ErasureRequests")).ShouldBe(1);
        (await fixture.OutboxAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Raises_that_race_for_one_subject_all_get_the_one_request_and_one_broadcast()
    {
        // Several at once, so a check-then-add that is not serialised loses on the unique index with a fault.
        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => PostAsync(Subject, PrivacyPermissions.Erase)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        Guid[] ids =
        [
            .. await Task.WhenAll(
                responses.Select(r => r.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken)))
        ];
        ids.Distinct().Count().ShouldBe(1, "a race for one subject answers with one request");
        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM privacy.ErasureRequests")).ShouldBe(1);
        (await fixture.OutboxAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Two_subjects_get_two_requests()
    {
        Guid first = await RaiseAsync(Subject);
        Guid second = await RaiseAsync(Other);

        second.ShouldNotBe(first);
        (await fixture.OutboxAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_status_route_shows_where_a_request_stands_and_never_who_it_is_for()
    {
        Guid id = await RaiseAsync(Subject);

        HttpResponseMessage response = await GetAsync(id, PrivacyPermissions.Erase);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain(Subject.ToString(), Case.Insensitive, "the status route names no subject (ADR-092)");

        using JsonDocument view = JsonDocument.Parse(body);
        view.RootElement.GetProperty("requestId").GetGuid().ShouldBe(id);
        view.RootElement.GetProperty("status").GetString().ShouldBe("Open");
        view.RootElement.GetProperty("responders").EnumerateArray().Select(r => r.GetString())
            .ShouldBe(["ordering", "payments", "shipping", "notifications", "bff"]);
        view.RootElement.GetProperty("dueAt").GetDateTimeOffset()
            .ShouldBe(view.RootElement.GetProperty("raisedAt").GetDateTimeOffset().AddDays(30));
    }

    [Fact]
    public async Task An_unknown_request_is_a_404()
    {
        HttpResponseMessage response = await GetAsync(Guid.CreateVersion7(), PrivacyPermissions.Erase);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_empty_subject_is_a_400_and_stages_nothing()
    {
        HttpResponseMessage response = await PostAsync(Guid.Empty, PrivacyPermissions.Erase);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fixture.OutboxAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("catalog:write")]
    public async Task Without_the_permission_a_principal_cannot_raise_or_read(string? permission)
    {
        HttpResponseMessage raise = await PostAsync(Subject, permission);
        HttpResponseMessage read = await GetAsync(Guid.CreateVersion7(), permission);

        raise.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        read.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM privacy.ErasureRequests")).ShouldBe(0);
    }

    [Fact]
    public async Task An_anonymous_caller_is_a_401()
    {
        using HttpClient client = fixture.Factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            Route,
            new { subjectId = Subject },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<Guid> RaiseAsync(Guid subject)
    {
        HttpResponseMessage response = await PostAsync(subject, PrivacyPermissions.Erase);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> PostAsync(Guid subject, string? permission)
    {
        using HttpClient client = fixture.Factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new { subjectId = subject })
        };
        Authenticate(request, permission);

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> GetAsync(Guid id, string? permission)
    {
        using HttpClient client = fixture.Factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, $"{Route}/{id}");
        Authenticate(request, permission);

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static void Authenticate(HttpRequestMessage request, string? permission)
    {
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        if (permission is not null)
            request.Headers.Add(TestAuthHandler.PermissionsHeader, permission);
    }
}
