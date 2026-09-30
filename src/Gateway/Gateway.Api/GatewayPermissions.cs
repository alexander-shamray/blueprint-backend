namespace Gateway.Api;

/// <summary>The permissions the gateway's own routes name (§10.2); a service registers its own (§11.4).</summary>
public static class GatewayPermissions
{
    public const string InventoryAdmin = "inventory:admin";
    public const string PaymentsAdmin = "payments:admin";
}
