using System.Reflection;
using System.Text.Json;
using Inventory.Api;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>
/// Every permission Inventory requires is one somebody can be granted.
/// </summary>
/// <remarks>
/// <para>
/// <b>PR-17 invented this test because Ordering shipped without it and was
/// wrong; every service PR since is one that could repeat the mistake.</b>
/// §11.4 states the rule in both directions and this is the second one: a
/// permission something requires and the realm cannot grant is a path
/// nobody can reach — worth checking again here rather than trusted from
/// Ordering's own pass, because <c>inventory:admin</c> is a role of its own
/// in the realm's <c>commerce-api</c> client and nothing before this PR
/// required it.
/// </para>
/// <para>
/// <c>RealmImportTests</c> in <c>Common.Web.Tests</c> asserts the same realm's
/// role list is closed and could not catch this — it compares against literals
/// because that assembly is a building block and cannot reference a host to
/// read its constants. The check has to run from the side that owns the
/// constant, which is here.
/// </para>
/// </remarks>
public sealed class GrantablePermissionTests
{
    /// <summary>The client that owns the permission roles (§11.5).</summary>
    private const string ResourceClient = "commerce-api";

    [Fact]
    public void Every_permission_an_inventory_endpoint_requires_is_a_role_the_realm_can_grant()
    {
        // Read off the type, not listed by hand. Naming it explicitly would
        // make this a second manual registry — a permission added to
        // InventoryPermissions and required by an endpoint would not enter
        // the assertion, which is the exact defect the test exists to
        // prevent.
        string[] required =
        [
            .. typeof(InventoryPermissions)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
        ];

        // The guard against the whole thing passing vacuously: a vocabulary
        // that emptied would satisfy every assertion below.
        required.ShouldNotBeEmpty();

        foreach (string permission in required)
        {
            Grantable().ShouldContain(
                permission,
                $"an Inventory endpoint requires '{permission}' (§11.4) and the realm's {ResourceClient} client " +
                "cannot grant it, so that path is 403 for every principal Keycloak can issue (§11.5)");
        }
    }

    private static string[] Grantable()
    {
        using JsonDocument realm = JsonDocument.Parse(
            File.ReadAllText(RepositoryFile("deploy/compose/keycloak/realm-export.json")));

        return
        [
            .. realm.RootElement
                .GetProperty("roles")
                .GetProperty("client")
                .GetProperty(ResourceClient)
                .EnumerateArray()
                .Select(r => r.GetProperty("name").GetString())
                .OfType<string>()
        ];
    }

    /// <summary>
    /// The same walk <c>RealmImportTests</c> makes, and for the same reason: a
    /// test asserting a repository file has to find it from a bin directory
    /// whose depth is a build detail.
    /// </summary>
    private static string RepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Platform.slnx")))
            directory = directory.Parent;

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"No Platform.slnx above '{AppContext.BaseDirectory}', so '{relativePath}' cannot be located.");
        }

        string path = Path.Combine(directory.FullName, relativePath);

        // An absent file must fail here rather than as an empty realm that
        // satisfies nothing and asserts nothing.
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"'{relativePath}' is not in the repository at '{directory.FullName}'.",
                path);
    }
}
