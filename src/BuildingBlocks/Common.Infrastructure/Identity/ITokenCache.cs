namespace Common.Infrastructure.Identity;

/// <summary>
/// §11.5's token source, behind a port so that <c>ClientCredentialsHandler</c>
/// names no HTTP client and no provider. One token fetch serves many calls.
/// </summary>
public interface ITokenCache
{
    /// <summary>
    /// A currently valid access token for <paramref name="scope"/>, fetched if
    /// none is cached or the cached one is close enough to expiry to be unsafe
    /// to attach.
    /// </summary>
    Task<string> GetAsync(string scope, CancellationToken ct);
}
