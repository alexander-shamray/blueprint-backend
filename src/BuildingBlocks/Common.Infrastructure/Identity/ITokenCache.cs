namespace Common.Infrastructure.Identity;

/// <summary>§11.5's token source, behind a port so <c>ClientCredentialsHandler</c> names no HTTP client.</summary>
public interface ITokenCache
{
    /// <summary>A valid token for <paramref name="scope"/>, fetched when none is cached or it nears expiry.</summary>
    Task<string> GetAsync(string scope, CancellationToken ct);
}
