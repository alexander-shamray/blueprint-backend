using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Assistant.Protocol;
using Common.Assistant.Providers;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Web.Bff.Assistant;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-151's routes through the real host: off by default, the caller's own, redacted, limited.</summary>
public sealed class AssistantEndpointTests
{
    private static readonly object Opening = new
    {
        client = new { name = "web", version = "1.0.0" },
        locale = "en-GB",
        catalogue = Array.Empty<object>(),
        clientTools = Array.Empty<object>()
    };

    private readonly Guid _customer = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_by_default_the_status_says_off_and_no_conversation_can_open()
    {
        await using BffFactory factory = new();
        using HttpClient client = As(factory, _customer);

        AssistantStatus? status = await client.GetFromJsonAsync<AssistantStatus>("/v1/assistant", Ct);
        HttpResponseMessage open = await client.PostAsJsonAsync("/v1/assistant/conversations", Opening, Ct);

        status.ShouldBe(AssistantStatus.Off);
        open.StatusCode.ShouldBe(HttpStatusCode.NotFound, "off maps the status route alone (AI assistant design §9.1)");
    }

    [Fact]
    public async Task An_anonymous_caller_is_challenged_whether_the_assistant_is_on_or_off()
    {
        await using BffFactory off = new();
        await using AssistantFactory on = new();
        using HttpClient anonymousOff = off.CreateClient();
        using HttpClient anonymousOn = on.CreateClient();

        (await anonymousOff.GetAsync("/v1/assistant", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymousOn.GetAsync("/v1/assistant", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymousOn.PostAsJsonAsync("/v1/assistant/conversations", Opening, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Routing hands the metadata to the server's body limit, which the test server does not have, so the
    // endpoints' metadata is what this reads.
    [Fact]
    public async Task Every_assistant_route_carries_the_body_limit()
    {
        await using AssistantFactory factory = new();
        using HttpClient client = As(factory, _customer);
        await OpenAsync(client);

        RouteEndpoint[] routes = [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/v1/assistant", StringComparison.Ordinal) == true)];

        routes.Length.ShouldBeGreaterThan(5);
        routes.ShouldAllBe(e =>
            e.Metadata.GetMetadata<IRequestSizeLimitMetadata>()!.MaxRequestBodySize == AssistantEndpoints.MaxBodyBytes);
    }

    [Fact]
    public async Task A_turn_sent_while_one_runs_is_a_409_with_its_code_and_a_bad_one_names_its_field()
    {
        await using AssistantFactory factory = new();
        using HttpClient client = As(factory, _customer);
        string id = await OpenAsync(client);
        factory.Provider.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        HttpResponseMessage first = await client.PostAsJsonAsync(
            $"/v1/assistant/conversations/{id}/turns",
            new { text = "Hello" },
            Ct);
        HttpResponseMessage second = await client.PostAsJsonAsync(
            $"/v1/assistant/conversations/{id}/turns",
            new { text = "Hello again" },
            Ct);
        factory.Provider.Hold.SetResult();
        HttpResponseMessage empty = await client.PostAsJsonAsync(
            $"/v1/assistant/conversations/{id}/turns",
            new { text = "" },
            Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString()
            .ShouldBe(AssistantEndpoints.TurnRunningCode);
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await empty.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("text", out _)
            .ShouldBeTrue();
    }

    [Fact]
    public async Task A_client_tool_the_read_only_policy_refuses_is_a_400_keyed_by_client_tools()
    {
        await using AssistantFactory factory = new();
        using HttpClient client = As(factory, _customer);
        var destructive = new
        {
            name = "empty_basket",
            title = "Empty the basket",
            description = "Removes every line.",
            inputSchema = new { type = "object" },
            annotations = new
            {
                readOnlyHint = false,
                destructiveHint = true,
                idempotentHint = true,
                openWorldHint = false
            }
        };

        HttpResponseMessage open = await client.PostAsJsonAsync(
            "/v1/assistant/conversations",
            new
            {
                client = new { name = "web", version = "1.0.0" },
                locale = "en-GB",
                catalogue = Array.Empty<object>(),
                clientTools = new[] { destructive }
            },
            Ct);

        open.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await open.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors")
            .TryGetProperty("clientTools", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task On_a_customer_opens_a_conversation_and_reads_the_reply()
    {
        await using AssistantFactory factory = new();
        using HttpClient client = As(factory, _customer);

        AssistantStatus? status = await client.GetFromJsonAsync<AssistantStatus>("/v1/assistant", Ct);
        string id = await OpenAsync(client);
        HttpResponseMessage turn = await client.PostAsJsonAsync(
            $"/v1/assistant/conversations/{id}/turns",
            new { text = "Where is my order?" },
            Ct);
        JsonElement view = await SettledAsync(client, id);

        status.ShouldNotBeNull().Enabled.ShouldBeTrue();
        turn.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        view.GetProperty("items").EnumerateArray()
            .Any(item => item.ToString().Contains(ScriptedProvider.Reply, StringComparison.Ordinal))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Another_customer_s_conversation_answers_exactly_as_an_unknown_one_does()
    {
        await using AssistantFactory factory = new();
        using HttpClient owner = As(factory, _customer);
        using HttpClient stranger = As(factory, Guid.CreateVersion7());
        string id = await OpenAsync(owner);

        HttpResponseMessage theirs = await stranger.GetAsync($"/v1/assistant/conversations/{id}", Ct);
        HttpResponseMessage unknown = await stranger.GetAsync("/v1/assistant/conversations/no-such-id", Ct);
        HttpResponseMessage events = await stranger.GetAsync($"/v1/assistant/conversations/{id}/events", Ct);
        HttpResponseMessage delete = await stranger.DeleteAsync($"/v1/assistant/conversations/{id}", Ct);

        theirs.StatusCode.ShouldBe(HttpStatusCode.NotFound, "403 would confirm the conversation exists (§10.7)");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        events.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        delete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.GetAsync($"/v1/assistant/conversations/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_customer_s_text_reaches_the_provider_with_its_contact_details_redacted()
    {
        await using AssistantFactory factory = new();
        using HttpClient client = As(factory, _customer);
        string id = await OpenAsync(client);

        await client.PostAsJsonAsync(
            $"/v1/assistant/conversations/{id}/turns",
            new { text = "Write to jo.bloggs@example.com or call +44 20 7946 0958 about my order." },
            Ct);
        await SettledAsync(client, id);

        string sent = factory.Provider.Requests.ShouldHaveSingleItem().History
            .OfType<UserMessage>().ShouldHaveSingleItem().Content;
        sent.ShouldNotContain("jo.bloggs@example.com");
        sent.ShouldNotContain("7946");
        sent.ShouldContain(CustomerRedactor.Email);
        sent.ShouldContain(CustomerRedactor.Phone);
    }

    [Fact]
    public async Task The_stream_carries_the_turn_s_text_and_its_end()
    {
        await using AssistantFactory factory = new();
        using HttpClient client = As(factory, _customer);
        string id = await OpenAsync(client);

        using HttpResponseMessage stream = await client.GetAsync(
            $"/v1/assistant/conversations/{id}/events",
            HttpCompletionOption.ResponseHeadersRead,
            Ct);
        await client.PostAsJsonAsync($"/v1/assistant/conversations/{id}/turns", new { text = "Hello" }, Ct);

        using StreamReader reader = new(await stream.Content.ReadAsStreamAsync(Ct));
        List<string> events = [];

        while (await reader.ReadLineAsync(Ct) is { } line && !events.Contains("event: turn_ended"))
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
                events.Add(line);
        }

        stream.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("text/event-stream");
        events.ShouldContain("event: text");
        events.ShouldContain("event: turn_ended");
    }

    [Fact]
    public async Task Starting_work_past_the_customer_s_rate_is_refused_with_429_and_another_customer_is_not()
    {
        await using AssistantFactory factory = new(
            Setting(nameof(AssistantOptions.TurnsPerMinute), "2"));
        using HttpClient client = As(factory, _customer);
        using HttpClient other = As(factory, Guid.CreateVersion7());

        await OpenAsync(client);
        await OpenAsync(client);
        HttpResponseMessage third = await client.PostAsJsonAsync("/v1/assistant/conversations", Opening, Ct);

        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        third.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");
        (await other.PostAsJsonAsync("/v1/assistant/conversations", Opening, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.OK, "the partition is the subject, so one customer's rate is not another's");
    }

    [Fact]
    public void A_provider_value_that_names_none_fails_the_start_naming_the_key()
    {
        using AssistantFactory factory = new(
            Setting(nameof(AssistantOptions.Provider), "openai"));

        Should.Throw<InvalidOperationException>(() => factory.CreateClient())
            .Message.ShouldContain("Assistant:Provider");
    }

    [Fact]
    public void A_key_named_like_a_secret_fails_the_start_whatever_its_value()
    {
        using AssistantFactory factory = new(Setting("ApiKey", "not-a-real-key"));

        Should.Throw<InvalidOperationException>(() => factory.CreateClient())
            .Message.ShouldContain("named like a secret");
    }

    private static KeyValuePair<string, string?> Setting(string key, string value) =>
        new($"{AssistantOptions.SectionName}:{key}", value);

    private static HttpClient As(BffFactory factory, Guid subject)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, subject.ToString());

        return client;
    }

    private static async Task<string> OpenAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/v1/assistant/conversations", Opening, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<ConversationOpened>(Ct)).ShouldNotBeNull().Id;
    }

    // The turn runs off the request, so the view is read until it says the turn is over.
    private static async Task<JsonElement> SettledAsync(HttpClient client, string id)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (true)
        {
            JsonElement view = await client.GetFromJsonAsync<JsonElement>($"/v1/assistant/conversations/{id}", Ct);

            if (!view.GetProperty("running").GetBoolean() && view.GetProperty("items").GetArrayLength() > 1)
                return view;

            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"The turn on {id} did not end within ten seconds.");

            await Task.Delay(50, Ct);
        }
    }
}
