using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Polly;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Carrier;

/// <summary>
/// The one place that knows the carrier's wire format (§3.2's anti-corruption
/// layer). Everything it returns is the port's vocabulary, and everything the
/// carrier sends is a stranger's input: bounded, translated, and never stored
/// as a link (spec, section 9).
/// </summary>
internal sealed class HttpCarrierGateway(HttpClient http, CarrierMetrics metrics, TimeProvider clock) : ICarrierGateway
{
    private const string KeyHeader = "Idempotency-Key";

    private sealed record AddressBody(string Line1, string? Line2, string City, string PostalCode, string Country);

    private sealed record BookBody(Guid ShipmentId, AddressBody Address);

    private sealed record BookAnswer(string Status, string? Reference, string? TrackingNumber, string? Code);

    private sealed record CancelAnswer(string Status, string? Code);

    // No link is declared, which is what makes "stores no URL" a property of
    // the type rather than of a line somebody could delete: System.Text.Json
    // drops what no member names.
    private sealed record EventAnswer(string? Id, string? Status, DateTimeOffset? OccurredAt);

    private sealed record EventsAnswer(IReadOnlyList<EventAnswer>? Events);

    public async Task<BookingResult> BookAsync(BookingRequest request, CancellationToken ct)
    {
        DeliveryAddress address = request.Address;
        using HttpRequestMessage message = new(HttpMethod.Post, "v1/shipments")
        {
            Content = JsonContent.Create(new BookBody(
                request.ShipmentId.Value,
                new AddressBody(address.Line1, address.Line2, address.City, address.PostalCode, address.Country)))
        };
        message.Headers.Add(KeyHeader, request.IdempotencyKey);

        using HttpResponseMessage response = await SendAsync(message, ct);

        // Only the two answers the wire format defines carry a body worth
        // reading; anything else is a carrier this adapter does not
        // understand, which is a fault rather than an answer.
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.UnprocessableEntity))
            throw Unavailable($"The carrier answered a booking with {(int)response.StatusCode}.");

        BookAnswer? answer = await ReadAsync<BookAnswer>(response, "a booking", ct);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            return answer is { Status: "refused", Code: { } code } && Recordable(code, CarrierLimits.MaxReasonLength)
                ? new BookingResult.Refused(code)
                : throw Unavailable("The carrier refused with a body that is not a refusal.");
        }

        return answer is { Status: "booked", Reference: { } reference, TrackingNumber: { } tracking }
               && Recordable(reference, CarrierLimits.MaxReferenceLength)
               && Recordable(tracking, CarrierLimits.MaxTrackingNumberLength)
            ? new BookingResult.Booked(reference, tracking)
            : throw Unavailable("The carrier booked with a body that is not a booking.");
    }

    public async Task<CancellationResult> CancelAsync(CancellationRequest request, CancellationToken ct)
    {
        using HttpRequestMessage message = new(
            HttpMethod.Post, $"v1/shipments/{Uri.EscapeDataString(request.Reference)}/cancel");
        message.Headers.Add(KeyHeader, request.IdempotencyKey);

        using HttpResponseMessage response = await SendAsync(message, ct);

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
            throw Unavailable($"The carrier answered a cancellation with {(int)response.StatusCode}.");

        CancelAnswer? answer = await ReadAsync<CancelAnswer>(response, "a cancellation", ct);

        // Section 6 turns each of these into a different terminal state, so a
        // body that disagrees with its status is a fault: guessing would void
        // a parcel that is moving, or leave a cancelled one on the row.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return answer is { Status: "too_late" }
                ? new CancellationResult.TooLate()
                : throw Unavailable("The carrier answered 409 with a body that is not a refusal to cancel.");
        }

        return answer is { Status: "cancelled" }
            ? new CancellationResult.Cancelled()
            : throw Unavailable("The carrier answered 200 with a body that is not a cancellation.");
    }

    public async Task<IReadOnlyList<CarrierEvent>> GetEventsAsync(string reference, CancellationToken ct)
    {
        using HttpRequestMessage message = new(
            HttpMethod.Get, $"v1/shipments/{Uri.EscapeDataString(reference)}/events");

        using HttpResponseMessage response = await SendAsync(message, ct);

        // An answer, not a fault: the carrier has not heard of the booking
        // yet, which is ordinary between the booking and the first scan.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        if (response.StatusCode != HttpStatusCode.OK)
            throw Unavailable($"The carrier answered an events read with {(int)response.StatusCode}.");

        EventsAnswer? answer = await ReadAsync<EventsAnswer>(response, "an events read", ct);

        if (answer?.Events is not { } page)
            throw Unavailable("The carrier answered an events read with a body that is not a page.");

        if (page.Count > CarrierHop.MaxEventsPerPage)
            throw Unavailable($"The carrier sent {page.Count} events on one page.");

        DateTimeOffset ceiling = clock.GetUtcNow() + CarrierHop.MaxClockSkew;

        return [.. page.Select(e => Translate(e, ceiling))];
    }

    private CarrierEvent Translate(EventAnswer answer, DateTimeOffset ceiling)
    {
        if (answer is not { Id: { } id, Status: { } status, OccurredAt: { } occurredAt }
            || !Recordable(id, CarrierLimits.MaxCarrierEventIdLength))
        {
            throw Unavailable("The carrier sent an event this adapter cannot key.");
        }

        // The page is refused whole rather than partly kept: a stored instant
        // ahead of the clock outranks every real one for ever, and section 5's
        // promotion is by rank and then by time.
        if (occurredAt > ceiling)
            throw Unavailable("The carrier sent an event later than this clock allows.");

        return new CarrierEvent(id, Rank(status), occurredAt);
    }

    // A carrier adds statuses on its own schedule, so a word this platform has
    // not agreed is Unrecognised and moves nothing (spec, section 5).
    private static TrackingStatus Rank(string status) => status switch
    {
        "collected" => TrackingStatus.Collected,
        "in_transit" => TrackingStatus.InTransit,
        "delivered" => TrackingStatus.Delivered,
        _ => TrackingStatus.Unrecognised
    };

    // The adapter's own rule: a blank or over-long string records nothing, so
    // both are refused before a row is written.
    private static bool Recordable(string value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private async Task<T?> ReadAsync<T>(HttpResponseMessage response, string act, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
        // A charset nobody can decode surfaces as InvalidOperationException
        // before the parser runs, and it is the same carrier being wrong.
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw Unavailable($"The carrier answered {act} with no JSON body.", e);
        }
    }

    // An answer the pipeline passed as a success, which this adapter cannot
    // read, is still an attempt that met a failing carrier (spec, section 11).
    // Statuses the pipeline retries, broken connections and timeouts are
    // counted inside it, and never here as well. The message never quotes the
    // body: a carrier-supplied string in a log is the link rule one layer up.
    private CarrierUnavailableException Unavailable(string message, Exception? inner = null)
    {
        metrics.Unavailable();
        return inner is null ? new(message) : new(message, inner);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(message, ct);
        }
        // ExecutionRejectedException is every refusal the pipeline makes on its
        // own account: a timeout, an open circuit, the concurrency limiter.
        catch (Exception e) when (e is HttpRequestException or ExecutionRejectedException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new CarrierUnavailableException("The carrier did not answer within the budget.", e);
        }

        if (CarrierAttemptCounter.IsTransient(response.StatusCode))
        {
            HttpStatusCode status = response.StatusCode;
            response.Dispose();
            throw new CarrierUnavailableException($"The carrier answered {(int)status} after every retry.");
        }

        return response;
    }
}
