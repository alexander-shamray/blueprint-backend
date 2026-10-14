using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Notifications.Application.Devices;

namespace Notifications.Infrastructure.Push;

/// <summary>Runs <see cref="FcmOptions"/>' annotations and the rules they cannot state, at start (§15.4).</summary>
internal sealed class FcmOptionsValidator : IValidateOptions<FcmOptions>
{
    public ValidateOptionsResult Validate(string? name, FcmOptions options)
    {
        List<string> failures = ProviderOptions.Annotated(options);
        ProviderOptions.Endpoint(failures, options.BaseUrl, FcmOptions.BaseUrlKey);
        ProviderOptions.Endpoint(failures, options.TokenUri, FcmOptions.TokenUriKey);
        ProviderOptions.Word(failures, options.ProjectId, FcmOptions.ProjectIdKey);
        ProviderOptions.Word(failures, options.ClientEmail, FcmOptions.ClientEmailKey);

        if (!string.IsNullOrWhiteSpace(options.PrivateKey) && !ProviderOptions.Imports(options.PrivateKey, RSA.Create))
            failures.Add($"{FcmOptions.PrivateKeyKey} is not an RSA key in PEM.");

        return ProviderOptions.Result(failures);
    }
}

/// <summary>Runs <see cref="ApnsOptions"/>' annotations and the rules they cannot state, at start (§15.4).</summary>
internal sealed class ApnsOptionsValidator : IValidateOptions<ApnsOptions>
{
    public ValidateOptionsResult Validate(string? name, ApnsOptions options)
    {
        List<string> failures = ProviderOptions.Annotated(options);
        ProviderOptions.Endpoint(failures, options.BaseUrl, ApnsOptions.BaseUrlKey);
        ProviderOptions.Word(failures, options.TeamId, ApnsOptions.TeamIdKey);
        ProviderOptions.Word(failures, options.KeyId, ApnsOptions.KeyIdKey);

        if (!string.IsNullOrWhiteSpace(options.PrivateKey) &&
            !ProviderOptions.Imports(options.PrivateKey, () => ECDsa.Create()))
        {
            failures.Add($"{ApnsOptions.PrivateKeyKey} is not an EC key in PEM.");
        }

        return ProviderOptions.Result(failures);
    }
}

/// <summary>The checks both providers' options share; no message echoes a value, as a start is logged.</summary>
internal static class ProviderOptions
{
    public static List<string> Annotated(object options)
    {
        List<ValidationResult> annotated = [];

        bool valid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            annotated,
            validateAllProperties: true);

        return valid
            ? []
            : [.. annotated.Select(a => a.ErrorMessage ?? "Invalid.")];
    }

    // Absolute and HTTP, with no credential in it: the bearer credential is the token, never the address.
    public static void Endpoint(List<string> failures, Uri? url, string key)
    {
        if (url is null)
            return;

        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            failures.Add($"{key} is not an absolute HTTP URL.");
        else if (url.UserInfo.Length > 0)
            failures.Add($"{key} carries a credential; the provider's is the signed token alone.");
    }

    // Each lands in a URL, a claim or a header, so a space or a control character would change its meaning.
    public static void Word(List<string> failures, string? value, string key)
    {
        if (value is not null && !PrintableAscii.Holds(value))
            failures.Add($"{key} is not one printable ASCII word.");
    }

    public static bool Imports<T>(string pem, Func<T> create)
        where T : AsymmetricAlgorithm
    {
        using T key = create();

        try
        {
            key.ImportFromPem(pem);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    public static ValidateOptionsResult Result(List<string> failures) =>
        failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
}
