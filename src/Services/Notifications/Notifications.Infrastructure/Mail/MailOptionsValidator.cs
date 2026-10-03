using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Notifications.Infrastructure.Mail;

/// <summary>Runs <see cref="MailOptions"/>' annotations and the rules they cannot state, at start (§15.4).</summary>
internal sealed class MailOptionsValidator : IValidateOptions<MailOptions>
{
    public ValidateOptionsResult Validate(string? name, MailOptions options)
    {
        List<ValidationResult> annotated = [];

        // No message echoes a configured value: a failed start is logged, and the password sits beside the rest.
        List<string> failures =
            Validator.TryValidateObject(options, new ValidationContext(options), annotated, validateAllProperties: true)
                ? []
                : [.. annotated.Select(a => a.ErrorMessage ?? "Invalid.")];

        if (options.Host is { } host && Uri.CheckHostName(host) == UriHostNameType.Unknown)
            failures.Add($"{MailOptions.HostKey} is not a host name.");

        if (options.From is { } from && !OneMailbox(from))
            failures.Add($"{MailOptions.FromKey} is not one mailbox.");

        bool hasUserName = !string.IsNullOrWhiteSpace(options.UserName);
        bool hasPassword = !string.IsNullOrWhiteSpace(options.Password);

        if (hasUserName != hasPassword)
            failures.Add($"{MailOptions.UserNameKey} and {MailOptions.PasswordKey} are set together or not at all.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // A display name is allowed; a second address, a group or a line break is not.
    private static bool OneMailbox(string text) =>
        !text.Any(char.IsControl)
        && MailboxAddress.TryParse(text, out MailboxAddress? parsed)
        && parsed is { Domain.Length: > 0 };
}
