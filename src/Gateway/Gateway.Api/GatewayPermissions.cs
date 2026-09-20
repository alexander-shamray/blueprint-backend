namespace Gateway.Api;

/// <summary>
/// The permissions the gateway itself names in a route (§10.2), as opposed to
/// the ones a service registers at its own composition root (§11.4). They are
/// here rather than in <c>Inventory.Api</c> and <c>Payments.Api</c> because the
/// routes that name them are the gateway's.
/// </summary>
/// <remarks>
/// <para>
/// A permission, not a role, and §10.2 says why the distinction is fragile
/// here in particular: a route file reads as infrastructure rather than as
/// authorization code, so <c>admin</c> — the role — is what creeps back in.
/// The names are <c>inventory:admin</c> and <c>payments:admin</c>, and the
/// shape is the platform's.
/// </para>
/// <para>
/// Constants rather than literals for §11.4's reason: each string is written
/// twice, in the policy registration and in <c>appsettings.json</c>, and only
/// one of those two sites is something the compiler can check. The other is
/// checked by <c>RouteConfigurationTests</c>, which is the half a constant
/// cannot close.
/// </para>
/// </remarks>
public static class GatewayPermissions
{
    public const string InventoryAdmin = "inventory:admin";
    public const string PaymentsAdmin = "payments:admin";
}
