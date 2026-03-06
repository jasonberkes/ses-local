using Microsoft.Extensions.Options;

namespace Ses.Local.Core.Options;

/// <summary>
/// Validates that all URL properties in <see cref="SesLocalOptions"/> are absolute HTTPS URIs.
/// Registered with ValidateOnStart() so misconfiguration is caught at startup.
/// </summary>
public sealed class SesLocalOptionsValidator : IValidateOptions<SesLocalOptions>
{
    public ValidateOptionsResult Validate(string? name, SesLocalOptions options)
    {
        var errors = new List<string>();

        ValidateUrl(options.IdentityBaseUrl,      nameof(options.IdentityBaseUrl),      errors);
        ValidateUrl(options.DocumentServiceBaseUrl, nameof(options.DocumentServiceBaseUrl), errors);
        ValidateUrl(options.MemoryBaseUrl,        nameof(options.MemoryBaseUrl),        errors);
        ValidateUrl(options.ClaudeAiBaseUrl,      nameof(options.ClaudeAiBaseUrl),      errors);
        ValidateUrl(options.SesMcpManifestUrl,    nameof(options.SesMcpManifestUrl),    errors);
        ValidateUrl(options.SesLocalManifestUrl,  nameof(options.SesLocalManifestUrl),  errors);
        ValidateUrl(options.DocsBaseUrl,          nameof(options.DocsBaseUrl),          errors);

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateUrl(string value, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{propertyName} must not be empty.");
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            errors.Add($"{propertyName} '{value}' is not a valid absolute URI.");
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
            errors.Add($"{propertyName} '{value}' must use HTTPS.");
    }
}
