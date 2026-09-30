using System.Reflection;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// Every permission the gateway requires is a role the shipped realm file can grant (§11.4).
/// </summary>
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

        // Read off the type, so a permission added to GatewayPermissions arrives here unasked.
        string[] required =
        [
            .. typeof(GatewayPermissions)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
        ];

        // A vocabulary that emptied would satisfy every assertion below.
        required.ShouldNotBeEmpty();

        foreach (string permission in required)
        {
            grantable.ShouldContain(
                permission,
                $"the gateway requires '{permission}' on a route (§10.2) and the realm's {ResourceClient} client " +
                "cannot grant it, so that path is 403 for every principal Keycloak can issue (§11.5)");
        }
    }

    /// <summary>Walks up to <c>Platform.slnx</c>, since the bin directory's depth is a build detail.</summary>
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

        // An absent file fails here rather than as an empty realm that asserts nothing.
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"'{relativePath}' is not in the repository at '{directory.FullName}'.",
                path);
    }
}
