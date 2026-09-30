using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Common.Infrastructure.Identity;

/// <summary>§11.5's handler, attached to every outbound client so that no call site has to remember it.</summary>
/// <remarks>Inside the resilience pipeline, so a retry asks <see cref="ITokenCache"/> again (§9.7).</remarks>
public sealed class ClientCredentialsHandler(ITokenCache tokens, IOptions<ServiceIdentityOptions> identity)
    : DelegatingHandler
{
    /// <remarks><c>cancellationToken</c>, not <c>ct</c>: CA1725 keeps the base name (ADR-019).</remarks>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string token = await tokens.GetAsync(identity.Value.Scope, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }
}
