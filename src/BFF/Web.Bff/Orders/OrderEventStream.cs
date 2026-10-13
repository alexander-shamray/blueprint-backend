using System.Globalization;
using System.Text.Json;
using Common.Application;
using Common.Web;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Web.Bff.Orders;

/// <summary>ADR-108's stream over the order read: the row polled by key, and nothing held between polls.</summary>
public sealed class OrderEventStream(OrderReader orders, TimeProvider clock, IOptions<JsonOptions> json)
{
    /// <summary>The event name a client listens for (§10.7).</summary>
    public const string EventName = "order";

    /// <summary>How often an open stream reads its row; an update reaches the client this much after the write.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Silence a proxy that closes an idle connection would not survive (§10.7).</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>Inside <c>ServiceOptions.OperationTimeout</c>, so the stream ends before the deadline answers 504.</summary>
    public static readonly TimeSpan Lifetime = ServiceOptions.OperationTimeout - TimeSpan.FromSeconds(5);

    /// <summary>The reconnection delay the stream asks of a client, so the server paces it (§10.7).</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(1);

    /// <summary>The event id: the row's <c>AsOf</c>, which a reconnecting client sends back as <c>Last-Event-ID</c>.</summary>
    public static string IdOf(DateTimeOffset asOf) => asOf.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Writes events until the lifetime, a delivered order, a vanished row or the client's departure.</summary>
    public async Task RunAsync(
        HttpContext context,
        Guid customerId,
        OrderDetail first,
        string? lastEventId,
        CancellationToken ct)
    {
        HttpResponse response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        long start = clock.GetTimestamp();
        string? lastSent = lastEventId;
        long lastWrite = start;
        OrderDetail current = first;

        try
        {
            await WriteAsync($"retry: {(int)RetryAfter.TotalMilliseconds}\n\n", response, ct);

            while (true)
            {
                string id = IdOf(current.AsOf);

                if (id != lastSent)
                {
                    string data = JsonSerializer.Serialize(current, json.Value.SerializerOptions);
                    await WriteAsync($"id: {id}\nevent: {EventName}\ndata: {data}\n\n", response, ct);
                    lastSent = id;
                    lastWrite = clock.GetTimestamp();
                }
                else if (clock.GetElapsedTime(lastWrite) >= HeartbeatInterval)
                {
                    await WriteAsync(": keep-alive\n\n", response, ct);
                    lastWrite = clock.GetTimestamp();
                }

                // Delivered is the one status nothing follows, so a client has nothing more to wait for.
                if (current.Status == BuyerStatuses.Delivered || clock.GetElapsedTime(start) + PollInterval >= Lifetime)
                    return;

                await Task.Delay(PollInterval, clock, ct);

                DateTimeOffset? asOf = await orders.AsOfAsync(customerId, current.OrderId, ct);

                // An erasure removes the row; the stream ends and the client's next read says why (§10.7).
                if (asOf is null)
                    return;

                if (IdOf(asOf.Value) == lastSent)
                    continue;

                Result<OrderDetail> read = await orders.FindAsync(customerId, current.OrderId, ct);

                if (read.IsFailure)
                    return;

                current = read.Value;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The buyer closed it; there is nobody to answer and nothing failed (§10.7).
        }
    }

    private static async Task WriteAsync(string frame, HttpResponse response, CancellationToken ct)
    {
        await response.WriteAsync(frame, ct);
        await response.Body.FlushAsync(ct);
    }
}
