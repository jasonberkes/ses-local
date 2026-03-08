namespace Ses.Local.Workers.Services;

/// <summary>
/// RFC 4648 §5 base64url encode/decode helpers.
/// </summary>
internal static class Base64Url
{
    /// <summary>Encodes bytes to a URL-safe base64 string (no padding).</summary>
    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    /// <summary>Decodes a URL-safe base64 string to bytes.</summary>
    public static byte[] Decode(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
        return Convert.FromBase64String(padded);
    }
}
