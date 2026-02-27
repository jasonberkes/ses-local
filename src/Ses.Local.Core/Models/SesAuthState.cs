namespace Ses.Local.Core.Models;

/// <summary>Current authentication state of ses-local.</summary>
public sealed class SesAuthState
{
    public bool IsAuthenticated { get; init; }
    public string? AccessToken { get; init; }
    public DateTime? AccessTokenExpiresAt { get; init; }
    public bool NeedsReauth { get; init; }

    public static SesAuthState Unauthenticated => new() { IsAuthenticated = false };
    public static SesAuthState ReauthRequired => new() { IsAuthenticated = false, NeedsReauth = true };
}
