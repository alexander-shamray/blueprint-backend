using Grpc.Core;
using Ordering.Delivery.V1;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Addresses;

/// <summary>
/// The client half of ADR-052's read, and the only code here that knows
/// Ordering's wire format. Every status it maps is that record's table.
/// </summary>
/// <remarks>
/// Every transient outcome — a status such as <c>Unavailable</c>, a refused
/// connection, an open circuit — escapes as <see cref="RpcException"/> and the
/// worker's backoff owns it; nothing branches on which one it was.
/// </remarks>
internal sealed class GrpcDeliveryAddressSource(
    DeliveryAddresses.DeliveryAddressesClient addresses,
    AddressMetrics metrics) : IDeliveryAddressSource
{
    public async Task<AddressLookup> GetAsync(OrderId orderId, CancellationToken ct)
    {
        GetDeliveryAddressReply reply;

        try
        {
            reply = await addresses.GetAsync(
                new GetDeliveryAddressRequest { OrderId = orderId.Value.ToString() },
                cancellationToken: ct);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
            // No such order, a cancelled one, or one whose address erasure has
            // cleared: one answer for the three (ADR-052), and terminal.
            return new AddressLookup.NoSuchOrder();
        }
        catch (RpcException e) when (e.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            metrics.Refused();

            throw new AddressSourceRefusedException(
                $"Ordering refused this host's token with {e.StatusCode} (ADR-052).", e);
        }
        catch (RpcException e) when (e.Status.DebugException is AddressSourceRefusedException refused)
        {
            // GrantCheckedTokenCache runs inside this client's handler chain,
            // and Grpc.Net.Client reports a handler's exception as an Internal
            // status of its own. The refusal was counted where it was decided.
            throw refused;
        }

        // Bounded here, before a row is written: a value wider than its column
        // would fail the insert with an error that quotes the value, and the
        // value is an address (AddressLimits).
        return new AddressLookup.Found(
            new DeliveryAddress(
                Bounded(reply.Line1, AddressLimits.MaxLineLength, "line1"),
                reply.Line2.Length == 0 ? null : Bounded(reply.Line2, AddressLimits.MaxLineLength, "line2"),
                Bounded(reply.City, AddressLimits.MaxCityLength, "city"),
                Bounded(reply.PostCode, AddressLimits.MaxPostalCodeLength, "post_code"),
                Country(reply.Country)),
            Guid.Parse(reply.CustomerId));

        // The field, never the value: the value is an address, and §13.4's
        // redactor cannot see one interpolated into a message.
        static string Bounded(string value, int maxLength, string field) =>
            value.Length <= maxLength
                ? value
                : throw new InvalidOperationException($"Ordering answered with a {field} longer than {maxLength}.");

        // Two ASCII letters, the shape the producer checks and the char column
        // stores; a shorter one would be padded into a different code.
        static string Country(string value) =>
            value.Length == AddressLimits.CountryLength && value.All(char.IsAsciiLetter)
                ? value
                : throw new InvalidOperationException(
                    $"Ordering answered with a country that is not {AddressLimits.CountryLength} ASCII letters.");
    }
}
