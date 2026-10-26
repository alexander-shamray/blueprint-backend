using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Reflection;
using System.Text.Json;
using Common.Assistant.Anthropic;
using Common.Assistant.Protocol;
using Shouldly;
using Web.Bff.Assistant;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>A provider stand-in on loopback, the variables the SDK reads aimed at it, and one host over both.</summary>
/// <remarks>
/// ADR-153's chart hands the host <c>ANTHROPIC_API_KEY</c> and the SDK reads <c>ANTHROPIC_BASE_URL</c>, so the client
/// is built as deployed. One host serves every test, as one over an unreachable broker is slow to stop.
/// </remarks>
public sealed class ProviderStub : IDisposable
{
    public const string Key = "sk-ant-test-not-a-real-key";

    private const string KeyVariable = "ANTHROPIC_API_KEY";

    private const string AddressVariable = "ANTHROPIC_BASE_URL";

    // A workstation's own values are put back, so a developer's key outlives the run.
    private readonly string? _key = Environment.GetEnvironmentVariable(KeyVariable);

    private readonly string? _address = Environment.GetEnvironmentVariable(AddressVariable);

    public ProviderStub()
    {
        // Loopback, not WireMock's default of every interface, which a workstation firewall stops to ask about.
        Server = WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });
        Environment.SetEnvironmentVariable(KeyVariable, Key);
        Environment.SetEnvironmentVariable(AddressVariable, Server.Urls[0]);
        Host = new ProviderFactory();
    }

    public WireMockServer Server { get; }

    public BffFactory Host { get; }

    public void Dispose()
    {
        Host.Dispose();
        Environment.SetEnvironmentVariable(KeyVariable, _key);
        Environment.SetEnvironmentVariable(AddressVariable, _address);
        Server.Stop();
    }

    /// <summary>The real BFF with the assistant on and the provider it builds itself, answered by the stub.</summary>
    private sealed class ProviderFactory : BffFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
        [
            .. base.Settings,
            new($"{AssistantOptions.SectionName}:{nameof(AssistantOptions.Provider)}", AssistantOptions.Anthropic)
        ];
    }
}

/// <summary>Serial, because the variables are the process's and every host built meanwhile would read them.</summary>
[CollectionDefinition(nameof(ProviderStubCollection), DisableParallelization = true)]
public sealed class ProviderStubCollection : ICollectionFixture<ProviderStub>;

/// <summary>§12.7's third-party row for the assistant: the SDK client the host builds, over real HTTP.</summary>
[Collection(nameof(ProviderStubCollection))]
public sealed class ProviderCallTests
{
    private const string MessagesPath = "/v1/messages";

    private static readonly object Opening = new
    {
        client = new { name = "web", version = "1.0.0" },
        locale = "en-GB",
        catalogue = Array.Empty<object>(),
        clientTools = Array.Empty<object>()
    };

    private readonly WireMockServer _server;

    private readonly BffFactory _host;

    public ProviderCallTests(ProviderStub stub)
    {
        _server = stub.Server;
        _host = stub.Host;

        // Each test starts with no stub another test added, and no request it made.
        _server.ResetLogEntries();
        _server.ResetMappings();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_streamed_reply_reaches_the_customer_from_a_request_carrying_the_environment_s_key()
    {
        Answer(Stream("text-with-cache"));
        using HttpClient client = As(_host);

        List<SseItem<string>> events = await TurnAsync(client);

        string.Concat(events.Where(e => e.EventType == "text").Select(e => Data(e).GetProperty("delta").GetString()))
            .ShouldBe("The logs are on the Logs screen.");
        Data(events[^1]).GetProperty("reason").GetString().ShouldBe(TurnEndReason.Done);
        ILogEntry sent = _server.LogEntries.ShouldHaveSingleItem();
        sent.RequestMessage!.Headers!
            .Single(h => h.Key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)).Value
            .ShouldHaveSingleItem().ShouldBe(ProviderStub.Key);
        JsonElement body = JsonDocument.Parse(sent.RequestMessage.Body!).RootElement;
        body.GetProperty("model").GetString().ShouldBe(AnthropicSettings.DefaultModel);
        body.GetProperty("stream").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_refusal_ends_the_turn_refused_with_the_provider_s_category()
    {
        Answer(Stream("refusal"));
        using HttpClient client = As(_host);

        JsonElement ended = Data((await TurnAsync(client))[^1]);

        ended.GetProperty("reason").GetString().ShouldBe(TurnEndReason.Refused);
        ended.GetProperty("category").GetString().ShouldBe("cyber");
    }

    [Fact]
    public async Task A_rejected_key_is_asked_once_and_ends_the_turn_in_an_error_that_does_not_carry_it()
    {
        Answer(Status(HttpStatusCode.Unauthorized, "authentication_error", "invalid x-api-key"));
        using HttpClient client = As(_host);

        List<SseItem<string>> events = await TurnAsync(client);

        Data(events.Single(e => e.EventType == "error")).GetProperty("status").GetInt32().ShouldBe(502);
        Data(events[^1]).GetProperty("reason").GetString().ShouldBe(TurnEndReason.Error);
        events.ShouldAllBe(e => !e.Data.Contains(ProviderStub.Key, StringComparison.Ordinal));
        _server.LogEntries.Count.ShouldBe(1, "a 401 is not one the SDK retries");
    }

    [Fact]
    public async Task An_overloaded_provider_is_asked_again_by_the_sdk_s_own_retries_before_the_turn_fails()
    {
        Answer(Status((HttpStatusCode)529, "overloaded_error", "Overloaded"));
        using HttpClient client = As(_host);

        List<SseItem<string>> events = await TurnAsync(client);

        Data(events[^1]).GetProperty("reason").GetString().ShouldBe(TurnEndReason.Error);
        _server.LogEntries.Count.ShouldBe(3, "the host leaves the SDK's default of two retries in place");
    }

    [Fact]
    public async Task The_status_reads_the_model_s_record_with_the_environment_s_key()
    {
        _server
            .Given(Request.Create().WithPath($"/v1/models/{AnthropicSettings.DefaultModel}").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new
                {
                    id = AnthropicSettings.DefaultModel,
                    type = "model",
                    display_name = "Claude",
                    created_at = "2026-08-01T00:00:00Z"
                })));
        using HttpClient client = As(_host);

        AssistantStatus? status = await client.GetFromJsonAsync<AssistantStatus>("/v1/assistant", Ct);

        status.ShouldBe(new AssistantStatus(true, AnthropicProvider.ProviderId, AnthropicSettings.DefaultModel, null));
    }

    private void Answer(IResponseBuilder response) =>
        _server.Given(Request.Create().WithPath(MessagesPath).UsingPost()).RespondWith(response);

    private static IResponseBuilder Stream(string recording) =>
        Response.Create()
            .WithStatusCode(HttpStatusCode.OK)
            .WithHeader("Content-Type", "text/event-stream")
            .WithBody(Recorded(recording));

    private static IResponseBuilder Status(HttpStatusCode status, string type, string message) =>
        Response.Create()
            .WithStatusCode(status)
            .WithHeader("Content-Type", "application/json")
            .WithBody(JsonSerializer.Serialize(new { type = "error", error = new { type, message } }));

    private static string Recorded(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"{name}.sse")
            ?? throw new InvalidOperationException($"There is no recorded stream named {name}.");
        using StreamReader reader = new(stream);

        return reader.ReadToEnd();
    }

    private static JsonElement Data(SseItem<string> item) => JsonDocument.Parse(item.Data).RootElement;

    private static HttpClient As(BffFactory factory)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        return client;
    }

    // The session's log replays from its first event, so the stream is read after the turn is started; the deadline
    // covers the SDK's backoff between retries.
    private static async Task<List<SseItem<string>>> TurnAsync(HttpClient client)
    {
        HttpResponseMessage opened = await client.PostAsJsonAsync("/v1/assistant/conversations", Opening, Ct);
        string id = (await opened.Content.ReadFromJsonAsync<ConversationOpened>(Ct)).ShouldNotBeNull().Id;
        (await client.PostAsJsonAsync($"/v1/assistant/conversations/{id}/turns", new { text = "Hello" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpResponseMessage stream = await client.GetAsync(
            $"/v1/assistant/conversations/{id}/events",
            HttpCompletionOption.ResponseHeadersRead,
            deadline.Token);
        List<SseItem<string>> events = [];

        await foreach (SseItem<string> item in SseParser
            .Create(await stream.Content.ReadAsStreamAsync(deadline.Token))
            .EnumerateAsync(deadline.Token))
        {
            events.Add(item);

            if (item.EventType == "turn_ended")
                return events;
        }

        throw new InvalidOperationException($"The stream on {id} ended before its turn did.");
    }
}
