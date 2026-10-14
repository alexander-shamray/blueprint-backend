using Common.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Notifications.Api.Tests;

/// <summary>The real API host over an unreachable database, with the production JWT scheme (§12.4).</summary>
public class NotificationsApiFactory : WebApplicationFactory<Program>
{
    // .invalid never resolves, so the check fails on NXDOMAIN; Connect Timeout=1 bounds a resolver that answers.
    internal const string UnreachableSql =
        "Server=notifications-sql.invalid,1433;Database=Notifications;User Id=sa;" +
        "Password=not-a-real-password;Encrypt=False;Connect Timeout=1";

    /// <summary>The authority every host must name (§11.3); <c>.invalid</c> never resolves.</summary>
    internal const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>All this host reads: the worker's broker, relay, contact source and windows are not its own.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("ConnectionStrings:Notifications", UnreachableSql)
            .UseSetting(AuthenticationExtensions.AuthorityKey, UnreachableAuthority);
}
