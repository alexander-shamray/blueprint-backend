using System.ComponentModel.DataAnnotations;

namespace Common.Infrastructure.Identity;

/// <summary>
/// §15.4's options type for §11.5's client-credentials grant. It earns a
/// binding by holding a secret that differs per environment, which is
/// §15.4's test; each host that calls a peer binds it for itself (§15.3).
/// </summary>
/// <remarks>
/// <c>[Required]</c> is what makes <c>ValidateDataAnnotations</c> do
/// anything: an unbound options class resolves to a default instance, and a
/// bound one with no annotations validates while empty.
/// </remarks>
public sealed class ServiceIdentityOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Identity:Client";

    [Required] public string ClientId { get; init; } = "";

    [Required] public string ClientSecret { get; init; } = "";

    [Required] public string Scope { get; init; } = "";
}
