namespace Inventory.Api;

/// <summary>
/// Inventory's permission vocabulary (§11.4): one name, the gateway's own
/// <c>inventory:admin</c> (§10.2), re-validated here because §11.3 makes
/// every service check its own token.
/// </summary>
public static class InventoryPermissions
{
    public const string Admin = "inventory:admin";
}
