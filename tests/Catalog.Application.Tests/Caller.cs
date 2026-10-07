using System.Security.Claims;
using Common.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>
/// Dispatches as a signed-in caller, by giving the request's accessor the principal <c>HttpContextCurrentUser</c>
/// reads, since an ownership check (ADR-074) has no subject to compare on a bare dispatch.
/// </summary>
internal static class Caller
{
    public static async Task<TResult> SendAsync<TResult>(
        IServiceProvider services,
        ICommand<TResult> command,
        Guid caller)
    {
        IHttpContextAccessor accessor = services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, caller.ToString())], "Test"))
        };

        try
        {
            return await services
                .GetRequiredService<IDispatcher>()
                .SendAsync(command, TestContext.Current.CancellationToken);
        }
        finally
        {
            // The accessor's holder is shared down the flow, so a principal left behind would follow later dispatches.
            accessor.HttpContext = null;
        }
    }
}
