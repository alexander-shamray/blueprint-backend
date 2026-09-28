using System.ComponentModel.DataAnnotations;

namespace Common.Infrastructure.Identity;

/// <summary>
/// §15.4's options type for §11.5's client-credentials grant. It earns a
/// binding by holding a secret that differs per environment, which is
/// §15.4's test; each host that calls a peer binds it for itself, and
/// <c>[Required]</c> is what its validation checks (§15.4).
/// </summary>
public sealed class ServiceIdentityOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Identity:Client";

    [Required] public string ClientId { get; init; } = "";

    [Required] public string ClientSecret { get; init; } = "";

    [Required] public string Scope { get; init; } = "";
}
