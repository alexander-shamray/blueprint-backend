using System.Net;
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
        string scope = identity.Value.Scope;
        string token = await tokens.GetAsync(scope, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        // Evicted and handed back, not retried: the next call fetches afresh (§11.5).
        if (Refused(response))
            tokens.Evict(scope, token);

        return response;
    }

    /// <summary>A 401, as a gRPC peer's authorization answers too; or a method's <c>Unauthenticated</c>.</summary>
    private static bool Refused(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Unauthorized ||
        (response.Headers.TryGetValues("grpc-status", out IEnumerable<string>? status) && status.Contains("16"));
}
