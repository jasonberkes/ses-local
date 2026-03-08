namespace Ses.Local.Workers.Tests;

/// <summary>Shared JWT test utilities used across multiple test classes.</summary>
internal static class TestJwtHelper
{
    /// <summary>Builds a minimal fake JWT with a valid <c>exp</c> claim.</summary>
    public static string CreateFakeJwt(DateTime expiry)
    {
        var header  = Base64UrlEncode("""{"alg":"HS256","typ":"JWT"}""");
        var exp     = new DateTimeOffset(expiry).ToUnixTimeSeconds();
        var payload = Base64UrlEncode($$$"""{"sub":"user-123","exp":{{{exp}}},"iat":1234567890}""");
        return $"{header}.{payload}.fakesig";
    }

    public static string Base64UrlEncode(string input) =>
        Ses.Local.Workers.Services.Base64Url.Encode(System.Text.Encoding.UTF8.GetBytes(input));
}
