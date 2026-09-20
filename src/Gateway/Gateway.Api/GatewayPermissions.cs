namespace Gateway.Api;

/// <summary>
/// The permissions the gateway itself names in a route (§10.2), as opposed to
/// the ones a service registers at its own composition root (§11.4). They are
/// here because the routes that name them are the gateway's. Permissions, not
/// roles, and §10.2 says why that slips here in particular: a route file reads
/// as infrastructure rather than as authorization code. Constants for §11.4's
/// reason — each string is written twice, in the policy registration and in
/// <c>appsettings.json</c>, and only the first is one the compiler can check;
/// <c>RouteConfigurationTests</c> closes the other half.
/// </summary>
public static class GatewayPermissions
{
    public const string InventoryAdmin = "inventory:admin";
    public const string PaymentsAdmin = "payments:admin";
}
