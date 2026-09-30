namespace Ordering.Api;

/// <summary>Ordering's permission vocabulary (§11.4), one policy per name in <c>Program.cs</c>.</summary>
/// <remarks>
/// <c>orders:delivery-address</c> guards ADR-052's <see cref="Grpc.DeliveryAddressService"/> and belongs to a host,
/// not a person; <c>orders:admin</c> is a claim, not a policy (§11.4).
/// </remarks>
public static class OrderingPermissions
{
    public const string Write = "orders:write";
    public const string Cancel = "orders:cancel";
    public const string DeliveryAddress = "orders:delivery-address";
}
