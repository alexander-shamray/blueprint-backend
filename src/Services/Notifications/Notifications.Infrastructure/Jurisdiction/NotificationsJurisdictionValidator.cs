using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Notifications.Application.Contacts;
using Notifications.Application.Rendering;

namespace Notifications.Infrastructure.Jurisdiction;

/// <summary>Refuses at start a jurisdiction the templates, the runtime or the contact rules cannot serve.</summary>
/// <remarks>
/// The annotations, then the rules between members and the files: every template the language set needs, a
/// culture for each language, an IANA zone, and a contact window no shorter than its stale ceiling (ADR-052, ADR-053).
/// </remarks>
internal sealed class NotificationsJurisdictionValidator(ContactOptions contacts)
    : IValidateOptions<NotificationsJurisdictionOptions>
{
    public ValidateOptionsResult Validate(string? name, NotificationsJurisdictionOptions options)
    {
        List<ValidationResult> annotated = [];
        List<string> failures =
            Validator.TryValidateObject(options, new ValidationContext(options), annotated, validateAllProperties: true)
                ? []
                : [.. annotated.Select(a => a.ErrorMessage ?? "Invalid.")];

        if (options.Languages is { } languages && options.TimeZone is { } timeZone)
        {
            try
            {
                failures.AddRange(TemplateRenderer.Refusals(TemplateSet.Embedded, languages, timeZone));
            }
            catch (TemplateSetException refused)
            {
                failures.AddRange(refused.Refusals);
            }
        }

        // A row deleted before its ceiling makes the ceiling a number nothing reaches (ADR-052).
        if (options.ContactRetention < contacts.StaleCeiling)
        {
            failures.Add(
                $"{NotificationsJurisdictionOptions.SectionName}:{nameof(options.ContactRetention)} is shorter " +
                $"than {nameof(ContactOptions)}.{nameof(ContactOptions.StaleCeiling)}, {contacts.StaleCeiling}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
