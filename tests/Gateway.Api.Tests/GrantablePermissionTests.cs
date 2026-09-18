using System.Reflection;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// Every permission the gateway requires is one somebody can be granted.
/// </summary>
/// <remarks>
/// <para>
/// <b>This test exists because PR-17 shipped without it and was wrong.</b> The
/// gateway registered a policy over <c>inventory:admin</c> and named it on a
/// route, and the realm's <c>commerce-api</c> client held one role —
/// <c>catalog:write</c>. So <c>/api/v1/inventory</c> answered 403 to every
/// principal the realm could issue, permanently, and nothing said a word:
/// §11.4's constant makes a misspelling a compile error and says nothing about
/// a name the identity provider has never heard of.
/// </para>
/// <para>
/// <c>RealmImportTests</c> in <c>Common.Web.Tests</c> asserts the same realm's
/// role list is closed, and could not catch this — it compares against a
/// literal because that assembly is a building block and cannot reference a
/// host to read its constants. The check has to run from the side that owns
/// the constant, which is here. <b>Catalog owes the same test</b>, and
/// <c>catalog:write</c> is grantable today only because PR-16 added the role
/// and the policy in one change rather than because anything checks it.
/// </para>
/// <para>
/// Reading the shipped realm file rather than a live Keycloak, on
/// <c>RealmImportTests</c>' terms: what is being asserted is a name in a
/// document, and a container would prove the same thing an order of magnitude
/// more slowly. Grantable is the bar, not granted — which development login
/// holds a permission is the realm's decision (§14.1), and
/// <c>RealmImportTests</c> pins it.
/// </para>
/// </remarks>
public sealed class GrantablePermissionTests
{
    /// <summary>The client that owns the permission roles (§11.5).</summary>
    private const string ResourceClient = "commerce-api";

    [Fact]
    public void Every_permission_the_gateway_requires_is_a_role_the_realm_can_grant()
    {
        using JsonDocument realm = JsonDocument.Parse(
            File.ReadAllText(RepositoryFile("deploy/compose/keycloak/realm-export.json")));

        string[] grantable =
        [
            .. realm.RootElement
                .GetProperty("roles")
                .GetProperty("client")
                .GetProperty(ResourceClient)
                .EnumerateArray()
                .Select(r => r.GetProperty("name").GetString())
                .OfType<string>()
        ];

        // Read off the type, not listed by hand. Naming `InventoryAdmin`
        // explicitly made this a second manual registry — a permission added to
        // GatewayPermissions and required by a route would not enter this
        // assertion, which is the exact defect the test exists to prevent,
        // one level up. Reflection over the constants is what makes a new one
        // arrive here without anybody remembering to bring it.
        string[] required =
        [
            .. typeof(GatewayPermissions)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
        ];

        // The guard against the whole thing passing vacuously: a vocabulary
        // that emptied would satisfy every assertion below.
        required.ShouldNotBeEmpty();

        foreach (string permission in required)
        {
            grantable.ShouldContain(
                permission,
                $"the gateway requires '{permission}' on a route (§10.2) and the realm's {ResourceClient} client " +
                "cannot grant it, so that path is 403 for every principal Keycloak can issue (§11.5)");
        }
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
