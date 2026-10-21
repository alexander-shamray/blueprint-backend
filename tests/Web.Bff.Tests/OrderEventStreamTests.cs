using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-108's stream through the real host: the owner check, the first event, a change, and the ends.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class OrderEventStreamTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _buyer = Guid.CreateVersion7();

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient As(Guid subject)
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, subject.ToString());

        return client;
    }

    private static async Task<HttpResponseMessage> OpenAsync(HttpClient client, Guid order, string? lastEventId = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"/v1/orders/{order}/events");

        if (lastEventId is not null)
            request.Headers.Add("Last-Event-ID", lastEventId);

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
    }

    /// <summary>The next frame, its lines joined; a heartbeat or the retry hint is a frame like any other.</summary>
    private static async Task<string?> NextFrameAsync(StreamReader reader)
    {
        List<string> lines = [];

        while (await reader.ReadLineAsync(Ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (lines.Count > 0)
                    return string.Join('\n', lines);

                continue;
            }

            lines.Add(line);
        }

        return lines.Count > 0 ? string.Join('\n', lines) : null;
    }

    private static async Task<string> NextOrderFrameAsync(StreamReader reader)
    {
        while (await NextFrameAsync(reader) is { } frame)
        {
            if (frame.Contains($"event: {OrderEventStream.EventName}", StringComparison.Ordinal))
                return frame;
        }

        throw new InvalidOperationException("The stream ended before an order event.");
    }

    /// <summary>The members that name the failure; the trace id differs per request and says nothing of it.</summary>
    private static async Task<(string? Code, string? Detail)> ProblemAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return (body.RootElement.GetProperty("code").GetString(), body.RootElement.GetProperty("detail").GetString());
    }

    private static JsonElement DataOf(string frame)
    {
        string data = frame.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];

        return JsonDocument.Parse(data).RootElement.Clone();
    }

    [Fact]
    public async Task An_anonymous_caller_is_challenged()
    {
        using HttpClient anonymous = fixture.Factory.CreateClient();

        HttpResponseMessage response = await OpenAsync(anonymous, Guid.CreateVersion7());

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Another_buyer_s_order_answers_the_404_an_unknown_one_does_before_any_stream_opens()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient stranger = As(Guid.CreateVersion7());

        using HttpResponseMessage theirs = await OpenAsync(stranger, order);
        using HttpResponseMessage unknown = await OpenAsync(stranger, Guid.CreateVersion7());

        theirs.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        theirs.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ProblemAsync(theirs)).ShouldBe(await ProblemAsync(unknown));
    }

    [Fact]
    public async Task The_first_event_is_the_detail_route_s_answer_with_the_rows_as_of_for_its_id()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient client = As(_buyer);
        string detail = await client.GetStringAsync($"/v1/orders/{order}", Ct);

        using HttpResponseMessage response = await OpenAsync(client, order);
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync(Ct));
        string frame = await NextOrderFrameAsync(reader);

        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("text/event-stream");
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        JsonElement data = DataOf(frame);
        data.GetRawText().ShouldBe(JsonDocument.Parse(detail).RootElement.GetRawText());
        frame.ShouldContain($"id: {OrderEventStream.IdOf(data.GetProperty("asOf").GetDateTimeOffset())}");
    }

    [Fact]
    public async Task A_change_to_the_row_reaches_the_open_stream_as_the_next_event()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient client = As(_buyer);

        using HttpResponseMessage response = await OpenAsync(client, order);
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync(Ct));
        DataOf(await NextOrderFrameAsync(reader)).GetProperty("status").GetString().ShouldBe(BuyerStatuses.Placed);

        await fixture.DeliverAsync(OrderEvents.Confirmed(order, _buyer, At.AddSeconds(1)));
        JsonElement next = DataOf(await NextOrderFrameAsync(reader));

        next.GetProperty("status").GetString().ShouldBe(BuyerStatuses.Confirmed);
    }

    [Fact]
    public async Task A_reconnect_naming_the_current_as_of_is_sent_no_event_and_a_delivered_order_ends_the_stream()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        await fixture.DeliverAsync(OrderEvents.Delivered(order, At.AddSeconds(1)));
        using HttpClient client = As(_buyer);
        OrderDetail current = (await client.GetFromJsonAsync<OrderDetail>($"/v1/orders/{order}", Ct))!;

        using HttpResponseMessage response = await OpenAsync(client, order, OrderEventStream.IdOf(current.AsOf));
        string body = await ReadBeforeHeartbeatAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotContain($"event: {OrderEventStream.EventName}");
        body.ShouldContain("retry:");
    }

    [Fact]
    public async Task A_delivered_order_is_sent_once_and_the_stream_then_ends()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        await fixture.DeliverAsync(OrderEvents.Delivered(order, At.AddSeconds(1)));
        using HttpClient client = As(_buyer);

        using HttpResponseMessage response = await OpenAsync(client, order);
        string body = await ReadBeforeHeartbeatAsync(response);

        body.Split($"event: {OrderEventStream.EventName}").Length.ShouldBe(2);
        body.ShouldContain("\"status\":\"delivered\"");
    }

    [Fact]
    public async Task An_idle_stream_sends_a_heartbeat_and_ends_by_itself_before_the_hosts_deadline()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient client = As(_buyer);
        client.Timeout = Common.Web.ServiceOptions.OperationTimeout;

        using HttpResponseMessage response = await OpenAsync(client, order);
        long started = TimeProvider.System.GetTimestamp();
        string body = await response.Content.ReadAsStringAsync(Ct);
        TimeSpan lasted = TimeProvider.System.GetElapsedTime(started);

        body.ShouldContain(": keep-alive");
        body.Split($"event: {OrderEventStream.EventName}").Length.ShouldBe(2, "an idle row sends its event once");
        lasted.ShouldBeGreaterThan(OrderEventStream.HeartbeatInterval);
        lasted.ShouldBeLessThan(Common.Web.ServiceOptions.OperationTimeout);
    }

    [Fact]
    public async Task A_stream_ends_when_its_row_is_gone()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient client = As(_buyer);

        using HttpResponseMessage response = await OpenAsync(client, order);
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync(Ct));
        await NextOrderFrameAsync(reader);

        await fixture.ExecuteAsync("DELETE FROM bff.OrderLines WHERE OrderId = {0}", order);
        await fixture.ExecuteAsync("DELETE FROM bff.Orders WHERE OrderId = {0}", order);

        // A heartbeat could not arrive inside this bound, so only the end reaches the reader's close.
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bound.CancelAfter(OrderEventStream.HeartbeatInterval);
        (await reader.ReadToEndAsync(bound.Token)).ShouldNotContain($"event: {OrderEventStream.EventName}");
    }

    /// <summary>The whole body, read inside the first heartbeat, so a delivered order is seen to end the stream and
    /// not its lifetime.</summary>
    private static async Task<string> ReadBeforeHeartbeatAsync(HttpResponseMessage response)
    {
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bound.CancelAfter(OrderEventStream.HeartbeatInterval);
        string body = await response.Content.ReadAsStringAsync(bound.Token);

        body.ShouldNotContain(": keep-alive");
        return body;
    }
}
