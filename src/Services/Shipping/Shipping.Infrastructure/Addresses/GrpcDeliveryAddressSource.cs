using Grpc.Core;
using Ordering.Delivery.V1;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Addresses;

/// <summary>The client half of ADR-052's read; every status it maps is that record's table.</summary>
/// <remarks>A transient outcome escapes as <see cref="RpcException"/> for the worker's backoff.</remarks>
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
            // One terminal answer for the three (ADR-052).
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
            // Grpc.Net.Client reports GrantCheckedTokenCache's refusal as Internal; it was counted there.
            throw refused;
        }

        // Bounded before a row is written, since a failed insert's error would quote an address.
        return new AddressLookup.Found(
            new DeliveryAddress(
                Required(reply.Line1, AddressLimits.MaxLineLength, "line1"),
                reply.Line2.Length == 0 ? null : Bounded(reply.Line2, AddressLimits.MaxLineLength, "line2"),
                Required(reply.City, AddressLimits.MaxCityLength, "city"),
                Required(reply.PostCode, AddressLimits.MaxPostalCodeLength, "post_code"),
                Country(reply.Country)),
            Customer(reply.CustomerId));

        // The field, never the value: §13.4's redactor cannot see an address interpolated into a message.
        static string Bounded(string value, int maxLength, string field) =>
            value.Length <= maxLength
                ? value
                : throw new InvalidOperationException($"Ordering answered with a {field} longer than {maxLength}.");

        // Address.Of refuses a blank one, so a blank reply is no address.
        static string Required(string value, int maxLength, string field) =>
            string.IsNullOrWhiteSpace(value)
                ? throw new InvalidOperationException($"Ordering answered with an empty {field}.")
                : Bounded(value, maxLength, field);

        // The form Address.Of stores; a shorter one would be padded into a different code.
        static string Country(string value) =>
            value.Length == AddressLimits.CountryLength && value.All(char.IsAsciiLetterUpper)
                ? value
                : throw new InvalidOperationException(
                    $"Ordering answered with a country that is not {AddressLimits.CountryLength} upper-case ASCII " +
                    "letters.");

        // The key erasure deletes by (ADR-052), so an empty one would hide the address from it.
        static Guid Customer(string value) =>
            Guid.TryParse(value, out Guid customer) && customer != Guid.Empty
                ? customer
                : throw new InvalidOperationException("Ordering answered with an empty or malformed customer_id.");
    }
}
