using System.ComponentModel.DataAnnotations;

namespace Notifications.Infrastructure.Push;

/// <summary>Where APNs is, and the team and key this deployment's provider tokens are minted under.</summary>
/// <remarks>Required only while an app names <c>apns</c>; the key is a secret, mounted as the relay's is.</remarks>
public sealed class ApnsOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Apns";

    public const string BaseUrlKey = $"{SectionName}:{nameof(BaseUrl)}";
    public const string TeamIdKey = $"{SectionName}:{nameof(TeamId)}";
    public const string KeyIdKey = $"{SectionName}:{nameof(KeyId)}";

    /// <summary>The setting's name, never its value.</summary>
    public const string PrivateKeyKey = $"{SectionName}:{nameof(PrivateKey)}";

    [Required]
    public Uri? BaseUrl { get; init; }

    /// <summary>The token's issuer.</summary>
    [Required]
    public string? TeamId { get; init; }

    /// <summary>The signing key's id, the token header's <c>kid</c>.</summary>
    [Required]
    public string? KeyId { get; init; }

    /// <summary>The P-256 signing key as PEM; generated in Development alone.</summary>
    public string? PrivateKey { get; init; }
}
