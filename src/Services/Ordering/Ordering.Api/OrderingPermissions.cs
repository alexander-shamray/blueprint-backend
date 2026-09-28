namespace Ordering.Api;

/// <summary>
/// Ordering's permission vocabulary (§11.4): the strings are the contract with
/// the realm's claim mapper (§11.5), and <c>Program.cs</c> registers a policy
/// per name. <c>orders:delivery-address</c> guards
/// <see cref="Grpc.DeliveryAddressService"/>, ADR-052's gRPC method; it
/// belongs to a host and to no person, because the read crosses subjects and
/// the method skips the ownership check. <c>orders:read</c> is absent until a
/// read endpoint requires it, and <c>orders:admin</c> is a claim
/// <c>CancelOrderHandler</c> checks against a loaded aggregate (§11.4).
/// </summary>
public static class OrderingPermissions
{
    public const string Write = "orders:write";
    public const string Cancel = "orders:cancel";
    public const string DeliveryAddress = "orders:delivery-address";
}
