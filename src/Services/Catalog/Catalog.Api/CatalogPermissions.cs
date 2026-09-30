namespace Catalog.Api;

/// <summary>Catalog's permission vocabulary (§11.4), the contract with the realm's claim mapper (§11.5).</summary>
/// <remarks>
/// A constant, so the policy and the endpoint cannot spell it differently (§11.4). No <c>catalog:read</c>:
/// reading is anonymous, and a permission nothing requires is a name nobody can act on.
/// </remarks>
public static class CatalogPermissions
{
    public const string Write = "catalog:write";
}
