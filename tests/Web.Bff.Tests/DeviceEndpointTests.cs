using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Shouldly;
using Web.Bff.Devices;
using Web.Bff.Endpoints;
using Web.Bff.TestSupport;
using Xunit;
using Outcome = Web.Bff.TestSupport.StubNotifications.Outcome;

namespace Web.Bff.Tests;

/// <summary>E.6.13's registration, driven end to end over a real gRPC server standing in for Notifications.</summary>
public sealed class DeviceEndpointTests : IAsyncLifetime
{
    private static readonly Guid Installation = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private const string CallerHeader = "Bearer caller-access-token";

    // A placeholder shaped like no provider's token, so nothing here reads as a credential.
    private const string DeviceHandle = "test-push-token-1";

    private readonly StubNotifications _notifications = new();

    private BffFactory _factory = null!;

    private static string Path => $"/v1/devices/{Installation}";

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

    /// <summary>A signed-in buyer, carrying an access token as the gateway forwards it.</summary>
    private HttpClient Caller()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "customer-1");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", CallerHeader);

        return client;
    }

    private static DeviceRegistration Registration(long sequence = 7) =>
        new("fcm", BffFactory.FcmApp, DeviceHandle, sequence);

    [Fact]
    public async Task A_registration_is_one_call_carrying_the_request_and_answers_204()
    {
        using HttpClient client = Caller();

        using HttpResponseMessage response = await client.PutAsJsonAsync(Path, Registration(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        StubNotifications.Call call = _notifications.Calls.ShouldHaveSingleItem();
        call.Method.ShouldBe("Register");
        call.InstallationId.ShouldBe(Installation.ToString("D"));
        call.Platform.ShouldBe("fcm");
        call.AppId.ShouldBe(BffFactory.FcmApp);
        call.Token.ShouldBe(DeviceHandle);
        call.Sequence.ShouldBe(7);
    }

    [Fact]
    public async Task The_hop_carries_the_callers_own_token_and_no_credential_of_the_BFFs()
    {
        using HttpClient client = Caller();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "corr-devices-1");

        using HttpResponseMessage response = await client.PutAsJsonAsync(Path, Registration(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        StubNotifications.Call call = _notifications.Calls.ShouldHaveSingleItem();

        // Unchanged, so Notifications takes the subject from the buyer's token (ADR-117).
        call.Authorization.ShouldBe(CallerHeader);
        call.CorrelationId.ShouldBe("corr-devices-1");

        // The pricing hop's token source was never asked, so no client credential reached this hop (§11.5).
        _factory.Tokens.Issued.ShouldBe(0);
    }

    [Fact]
    public async Task A_repeat_is_answered_as_the_first_was()
    {
        _notifications.AnswerNextWith.Enqueue(new(Outcome.Repeated));
        using HttpClient client = Caller();

        using HttpResponseMessage response = await client.PutAsJsonAsync(Path, Registration(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_stale_registration_is_a_409_carrying_the_rows_sequence()
    {
        _notifications.AnswerNextWith.Enqueue(new(Outcome.Stale, 42));
        using HttpClient client = Caller();

        using HttpResponseMessage response = await client.PutAsJsonAsync(Path, Registration(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe(DeviceEndpoints.StaleSequenceCode);
        problem.GetProperty("sequence").GetInt64().ShouldBe(42);
    }

    [Fact]
    public async Task A_sign_out_names_the_installation_and_its_sequence_and_answers_204()
    {
        using HttpClient client = Caller();

        using HttpResponseMessage response = await client.DeleteAsync($"{Path}?sequence=9", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        StubNotifications.Call call = _notifications.Calls.ShouldHaveSingleItem();
        call.Method.ShouldBe("SignOut");
        call.InstallationId.ShouldBe(Installation.ToString("D"));
        call.Sequence.ShouldBe(9);
        call.Authorization.ShouldBe(CallerHeader);
    }

    [Fact]
    public async Task A_stale_sign_out_is_a_409_carrying_the_rows_sequence()
    {
        _notifications.AnswerNextWith.Enqueue(new(Outcome.Stale, 12));
        using HttpClient client = Caller();

        using HttpResponseMessage response = await client.DeleteAsync($"{Path}?sequence=9", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("sequence").GetInt64().ShouldBe(12);
    }

    /// <summary>Each of ADR-117's refusals, answered before the hop, so none spends §9.7's budget.</summary>
    [Theory]
    [InlineData("gcm", BffFactory.FcmApp, DeviceHandle, 7, "Platform")]
    [InlineData("fcm", "com.example.unknown", DeviceHandle, 7, "AppId")]
    [InlineData("fcm", BffFactory.ApnsApp, DeviceHandle, 7, "AppId")]
    [InlineData("fcm", BffFactory.FcmApp, "has a space", 7, "Token")]
    [InlineData("fcm", BffFactory.FcmApp, "", 7, "Token")]
    [InlineData("fcm", BffFactory.FcmApp, DeviceHandle, 0, "Sequence")]
    public async Task A_malformed_registration_is_a_400_and_no_call(
        string platform,
        string appId,
        string token,
        long sequence,
        string field)
    {
        using HttpClient client = Caller();

        using HttpResponseMessage response =
            await client.PutAsJsonAsync(Path, new DeviceRegistration(platform, appId, token, sequence), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain(field);
        _notifications.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_token_past_its_bound_is_refused_without_being_echoed()
    {
        string token = new('a', DeviceRules.MaxTokenLength + 1);
        using HttpClient client = Caller();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            Path,
            new DeviceRegistration("fcm", BffFactory.FcmApp, token, 7),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The token is personal data (ADR-117), so a refusal names the field and never repeats the value.
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain(token[..64]);
        _notifications.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("?sequence=0")]
    [InlineData("?sequence=-1")]
    [InlineData("")]
    public async Task A_sign_out_without_a_positive_sequence_is_a_400_and_no_call(string query)
    {
        using HttpClient client = Caller();

        using HttpResponseMessage response = await client.DeleteAsync(Path + query, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _notifications.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_installation_id_that_is_not_a_UUID_matches_no_route()
    {
        using HttpClient client = Caller();

        using HttpResponseMessage response =
            await client.PutAsJsonAsync("/v1/devices/not-a-uuid", Registration(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _notifications.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_anonymous_caller_is_a_401_and_no_call()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(Path, Registration(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _notifications.Calls.ShouldBeEmpty();
    }

    /// <summary>A refused token is a 500, as on the pricing hop: this host validated it first (§10.5).</summary>
    [Theory]
    [InlineData(StatusCode.Unauthenticated, HttpStatusCode.InternalServerError)]
    [InlineData(StatusCode.InvalidArgument, HttpStatusCode.BadRequest)]
    [InlineData(StatusCode.Unavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task A_status_Notifications_raises_is_answered_from_the_upstream_table(
        StatusCode status,
        HttpStatusCode expected)
    {
        _notifications.FailNextWith.Enqueue(status);
        using HttpClient client = Caller();

        using HttpResponseMessage response = await client.PutAsJsonAsync(Path, Registration(), Ct);

        response.StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task A_dropped_connection_is_retried_since_both_methods_are_convergent()
    {
        _notifications.AbortNextCalls = 1;
        using HttpClient client = Caller();

        using HttpResponseMessage response = await client.PutAsJsonAsync(Path, Registration(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _notifications.Calls.Count.ShouldBe(2);

        // Each attempt runs the handler again, and still carries the caller's token once.
        _notifications.Calls.ShouldAllBe(call => call.Authorization == CallerHeader);
    }

    /// <summary>§9.7's rule 6: a reply the contract forbids is a 500, never a 204 or a 409.</summary>
    [Theory]
    [InlineData(Outcome.Unspecified, null)]
    [InlineData(Outcome.Applied, 8L)]
    [InlineData(Outcome.Repeated, 6L)]
    [InlineData(Outcome.Stale, 0L)]
    public async Task A_reply_the_contract_forbids_is_a_500(Outcome outcome, long? sequence)
    {
        _notifications.AnswerNextWith.Enqueue(new(outcome, sequence));
        using HttpClient client = Caller();

        using HttpResponseMessage response = await client.PutAsJsonAsync(Path, Registration(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }
}
