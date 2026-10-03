using System.ComponentModel.DataAnnotations;

namespace Notifications.Infrastructure.Mail;

/// <summary>Where the relay is, who sends, and how the session is protected (§15.4).</summary>
/// <remarks>Earns its options type as <c>Identity:Client</c> does, by a credential per environment (§15.4).</remarks>
public sealed class MailOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Mail";

    public const string HostKey = $"{SectionName}:{nameof(Host)}";
    public const string PortKey = $"{SectionName}:{nameof(Port)}";
    public const string FromKey = $"{SectionName}:{nameof(From)}";
    public const string SecurityKey = $"{SectionName}:{nameof(Security)}";
    public const string UserNameKey = $"{SectionName}:{nameof(UserName)}";

    /// <summary>The setting's name, never its value.</summary>
    public const string PasswordKey = $"{SectionName}:{nameof(Password)}";

    [Required]
    public string? Host { get; init; }

    [Required]
    [Range(1, 65535)]
    public int? Port { get; init; }

    /// <summary>One mailbox, a display name allowed; every <c>Message-ID</c> is minted under its domain.</summary>
    [Required]
    public string? From { get; init; }

    /// <summary>Nullable, so <c>[Required]</c> sees a missing key rather than the first member.</summary>
    [Required]
    public MailSecurity? Security { get; init; }

    public string? UserName { get; init; }

    public string? Password { get; init; }
}
