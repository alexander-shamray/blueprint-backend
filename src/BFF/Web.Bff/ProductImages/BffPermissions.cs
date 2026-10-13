namespace Web.Bff.ProductImages;

/// <summary>The permission this host checks itself (§11.4); every other check is a service's.</summary>
/// <remarks>
/// Catalog's <c>catalog:write</c>, spelled again because a host cannot reference another's assembly (§4.3); the realm's
/// claim mapper issues the string (§11.5), so a rename there is a rename here.
/// </remarks>
public static class BffPermissions
{
    public const string CatalogWrite = "catalog:write";
}
