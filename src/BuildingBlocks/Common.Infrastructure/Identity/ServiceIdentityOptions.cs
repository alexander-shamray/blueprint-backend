using System.ComponentModel.DataAnnotations;

namespace Common.Infrastructure.Identity;

/// <summary>§15.4's options for §11.5's client-credentials grant, bound by each host that calls a peer.</summary>
public sealed class ServiceIdentityOptions
{
    public const string SectionName = "Identity:Client";

    [Required] public string ClientId { get; init; } = "";

    [Required] public string ClientSecret { get; init; } = "";

    [Required] public string Scope { get; init; } = "";
}
