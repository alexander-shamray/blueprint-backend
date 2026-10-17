using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Shouldly;
using Web.Bff.Endpoints;
using Web.Bff.TestSupport;
using Xunit;
using SetOutcome = Web.Bff.TestSupport.StubNotifications.SetOutcome;
using Source = Web.Bff.TestSupport.StubNotifications.Source;

namespace Web.Bff.Tests;

/// <summary>E.6.14's preferences, driven end to end over a real gRPC server standing in for Notifications.</summary>
public sealed class PreferenceEndpointTests : IAsyncLifetime
{
    private const string CallerHeader = "Bearer caller-access-token";
    private const string Path = "/v1/notification-preferences/shipment-dispatched/push";

    private static readonly string Version = Guid.Parse("0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b").ToString("D");
    private static readonly DateTimeOffset SetAt = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly StubNotifications _notifications = new();

    private BffFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _notifications.InitializeAsync();

        _factory = new BffFactory { NotificationsAddress = _notifications.Address };
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _notifications.DisposeAsync();
    }

    private HttpClient Caller()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "customer-1");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", CallerHeader);

        return client;
    }

    private async Task<HttpResponseMessage> PutAsync(
        object? body,
        string? ifMatch = null,
        string? ifNoneMatch = null,
        string path = Path)
    {
        using HttpClient client = Caller();
        using HttpRequestMessage request = new(HttpMethod.Put, path) { Content = JsonContent.Create(body) };

        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task The_read_answers_every_pair_the_service_returns_in_its_order()
    {
        _notifications.Preferences =
        [
            new("order-placed", "email", true, Source.Required),
            new("order-placed", "push", true, Source.Default),
            new("shipment-dispatched", "push", false, Source.Set, SetAt, Version)
        ];
        using HttpClient client = Caller();

        using HttpResponseMessage response = await client.GetAsync("/v1/notification-preferences", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement[] pairs = [.. (await JsonAsync(response)).GetProperty("preferences").EnumerateArray()];
        pairs.Select(p => p.GetProperty("source").GetString()).ShouldBe(["required", "default", "set"]);
        pairs[0].GetProperty("version").ValueKind.ShouldBe(JsonValueKind.Null);
        pairs[2].GetProperty("version").GetString().ShouldBe(Version);
        pairs[2].GetProperty("optedIn").GetBoolean().ShouldBeFalse();
        pairs[2].GetProperty("setAt").GetDateTimeOffset().ShouldBe(SetAt);

        StubNotifications.PreferenceCall call = _notifications.PreferenceCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe(CallerHeader, "the caller's own token, as on the device hop (ADR-120)");
        _factory.Tokens.Issued.ShouldBe(0);
    }

    [Fact]
    public async Task A_write_under_its_version_carries_it_and_answers_the_new_one_as_body_and_etag()
    {
        using HttpResponseMessage response = await PutAsync(new { optedIn = false }, ifMatch: $"\"{Version}\"");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await JsonAsync(response);
        string written = body.GetProperty("version").GetString()!;
        response.Headers.ETag!.Tag.ShouldBe($"\"{written}\"");
        body.GetProperty("source").GetString().ShouldBe("set");

        StubNotifications.PreferenceCall call = _notifications.PreferenceCalls.ShouldHaveSingleItem();
        (call.Method, call.Topic, call.Channel, call.OptedIn, call.ReadVersion)
            .ShouldBe(("Set", "shipment-dispatched", "push", false, Version));
    }

    [Fact]
    public async Task A_first_write_states_if_none_match_star_and_the_hop_carries_no_version()
    {
        using HttpResponseMessage response = await PutAsync(new { optedIn = true }, ifNoneMatch: "*");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _notifications.PreferenceCalls.ShouldHaveSingleItem().ReadVersion.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, null, "If-Match")]
    [InlineData("\"0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b\"", "*", "If-Match")]
    [InlineData("W/\"0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b\"", null, "If-Match")]
    [InlineData("*", null, "If-Match")]
    [InlineData("\"not-a-version\"", null, "If-Match")]
    [InlineData("0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", null, "If-Match")]
    [InlineData(null, "\"0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b\"", "If-None-Match")]
    public async Task A_write_without_one_well_formed_precondition_is_a_400_and_no_hop(
        string? ifMatch,
        string? ifNoneMatch,
        string field)
    {
        using HttpResponseMessage response = await PutAsync(new { optedIn = true }, ifMatch, ifNoneMatch);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        _notifications.PreferenceCalls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/v1/notification-preferences/newsletter/push", "Topic")]
    [InlineData("/v1/notification-preferences/shipment-dispatched/sms", "Channel")]
    public async Task A_pair_outside_the_vocabulary_is_a_400_and_no_hop(string path, string field)
    {
        using HttpResponseMessage response = await PutAsync(new { optedIn = true }, ifNoneMatch: "*", path: path);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        _notifications.PreferenceCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_body_without_its_choice_is_a_400_rather_than_a_silent_withdrawal()
    {
        using HttpResponseMessage response = await PutAsync(new { }, ifNoneMatch: "*");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _notifications.PreferenceCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_lost_race_is_a_412_carrying_the_pair_as_it_stands()
    {
        _notifications.AnswerNextSetWith.Enqueue(
            new(SetOutcome.PreconditionFailed, new("shipment-dispatched", "push", false, Source.Set, SetAt, Version)));

        using HttpResponseMessage response = await PutAsync(new { optedIn = true }, ifNoneMatch: "*");

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        JsonElement problem = await JsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe(PreferenceEndpoints.PreconditionFailedCode);
        problem.GetProperty("preference").GetProperty("version").GetString().ShouldBe(Version);
        problem.GetProperty("preference").GetProperty("optedIn").GetBoolean().ShouldBeFalse();
        response.Headers.ETag!.Tag.ShouldBe($"\"{Version}\"");
    }

    [Fact]
    public async Task A_required_pair_is_a_422()
    {
        _notifications.AnswerNextSetWith.Enqueue(new(SetOutcome.Required, null));

        using HttpResponseMessage response = await PutAsync(new { optedIn = false }, ifNoneMatch: "*");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await JsonAsync(response)).GetProperty("code").GetString().ShouldBe(PreferenceEndpoints.RequiredCode);
    }

    [Theory]
    [InlineData(SetOutcome.Unspecified)]
    [InlineData(SetOutcome.Applied)]
    public async Task A_reply_the_contract_forbids_is_a_500(SetOutcome outcome)
    {
        // An unset outcome, or an applied write answered with a pair it did not ask for (§9.7's rule 6).
        _notifications.AnswerNextSetWith.Enqueue(
            new(outcome, new("order-placed", "email", true, Source.Set, SetAt, Version)));

        using HttpResponseMessage response = await PutAsync(new { optedIn = true }, ifNoneMatch: "*");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_set_pair_without_its_version_is_a_500()
    {
        _notifications.Preferences = [new("order-placed", "push", true, Source.Set, SetAt)];
        using HttpClient client = Caller();

        using HttpResponseMessage response = await client.GetAsync("/v1/notification-preferences", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task An_unavailable_service_is_a_503()
    {
        for (int attempt = 0; attempt <= NotificationsHop.MaxRetryAttempts; attempt++)
            _notifications.FailNextWith.Enqueue(StatusCode.Unavailable);

        using HttpResponseMessage response = await PutAsync(new { optedIn = true }, ifNoneMatch: "*");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task An_anonymous_caller_is_a_401_and_no_hop()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/v1/notification-preferences", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _notifications.PreferenceCalls.ShouldBeEmpty();
    }
}
