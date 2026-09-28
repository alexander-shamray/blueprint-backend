using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Common.Infrastructure.Identity;

/// <summary>
/// §11.5's handler, attached to every outbound client so that no call site has
/// to remember it.
/// </summary>
/// <remarks>
/// <b>It must sit inside the resilience pipeline</b>, which is what registering
/// it <i>after</i> <c>AddStandardResilienceHandler</c> does (§9.7). Outside it
/// the handler runs once per logical request rather than once per attempt, so
/// every retry replays the token the first attempt built.
/// <para>
/// What that buys is narrower than "a retry after a 401", which is how §11.5
/// used to put it and which its own configuration rules out — the standard
/// handler retries 5xx, 408 and <c>HttpRequestException</c>, and this hop's
/// callee answers gRPC statuses on an HTTP 200 besides. The real case is that
/// <i>whenever</i> a retry fires — a transport fault — the repeated attempt
/// asks <see cref="ITokenCache"/> again rather than replaying the first
/// attempt's token.
/// <para>
/// <b>Which usually returns the same token, and that is not a defect.</b>
/// <c>CachingTokenClient</c> serves a cached token until its expiry guard, so
/// the bytes are typically identical; what the position buys is that a token
/// which expired between attempts is refreshed for the next one. Narrower than
/// "carries a freshly fetched token", which is how this read until a review
/// checked it against the cache's own behaviour.
/// </para>
/// </para>
/// </remarks>
public sealed class ClientCredentialsHandler(ITokenCache tokens, IOptions<ServiceIdentityOptions> identity)
    : DelegatingHandler
{
    /// <remarks>
    /// The parameter is <c>cancellationToken</c> and not this repository's
    /// usual <c>ct</c>, because CA1725 requires an override to keep the base
    /// declaration's name and ADR-019 makes that an error. §11.5 prints
    /// <c>ct</c> and was amended in this change — the same correction §7.2's
    /// <c>ConfigureConventions</c> sample already took, and for the same
    /// reason: a reader consulting the framework's documentation is reading
    /// about the base name.
    /// </remarks>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Cached until shortly before expiry; one token fetch serves many calls.
        string token = await tokens.GetAsync(identity.Value.Scope, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }
}
