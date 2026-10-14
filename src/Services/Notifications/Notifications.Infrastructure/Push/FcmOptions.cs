using System.ComponentModel.DataAnnotations;

namespace Notifications.Infrastructure.Push;

/// <summary>Where FCM and Google's token endpoint are, and the service account this deployment sends as.</summary>
/// <remarks>Required only while an app names <c>fcm</c>; its key is a secret, as the relay's is (§15.4).</remarks>
public sealed class FcmOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Fcm";

    public const string BaseUrlKey = $"{SectionName}:{nameof(BaseUrl)}";
    public const string TokenUriKey = $"{SectionName}:{nameof(TokenUri)}";
    public const string ProjectIdKey = $"{SectionName}:{nameof(ProjectId)}";
    public const string ClientEmailKey = $"{SectionName}:{nameof(ClientEmail)}";

    /// <summary>The setting's name, never its value.</summary>
    public const string PrivateKeyKey = $"{SectionName}:{nameof(PrivateKey)}";

    [Required]
    public Uri? BaseUrl { get; init; }

    /// <summary>The OAuth 2.0 token endpoint the signed assertion is exchanged at, and its audience.</summary>
    [Required]
    public Uri? TokenUri { get; init; }

    [Required]
    public string? ProjectId { get; init; }

    /// <summary>The service account the assertion is issued by.</summary>
    [Required]
    public string? ClientEmail { get; init; }

    /// <summary>The service account's RSA key as PEM; generated in Development alone.</summary>
    public string? PrivateKey { get; init; }
}
