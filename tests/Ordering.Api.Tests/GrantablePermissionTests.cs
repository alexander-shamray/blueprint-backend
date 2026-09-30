using System.Reflection;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>§11.4's rule in its second direction: every permission Ordering requires, the realm can grant.</summary>
public sealed class GrantablePermissionTests
{
    /// <summary>The client that owns the permission roles (§11.5).</summary>
    private const string ResourceClient = "commerce-api";

    [Fact]
    public void Every_permission_an_ordering_endpoint_requires_is_a_role_the_realm_can_grant()
    {
        // Read off the type, not listed by hand, so a new permission enters the assertion.
        string[] required =
        [
            .. typeof(OrderingPermissions)
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
                $"an Ordering endpoint requires '{permission}' (§11.4) and the realm's {ResourceClient} client " +
                "cannot grant it, so that path is 403 for every principal Keycloak can issue (§11.5)");
        }
    }

    [Fact]
    public void The_admin_claim_is_grantable_though_no_policy_names_it()
    {
        // Spelt by hand: a claim, not a policy (§11.4), so OrderingPermissions has no constant to reflect on.
        Grantable().ShouldContain("orders:admin");
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

    /// <summary>Walks up from the bin directory, whose depth is a build detail.</summary>
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
