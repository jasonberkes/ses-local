using Microsoft.Extensions.Logging.Abstractions;
using Ses.Local.Core.Interfaces;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class AuthServiceTests
{
    private static AuthService BuildSut(
        ICredentialStore? keychain = null,
        IdentityClient? identity = null)
    {
        keychain ??= new InMemoryCredentialStore();
        identity ??= BuildIdentityClient(null);
        return new AuthService(keychain, identity, NullLogger<AuthService>.Instance);
    }

    private static IdentityClient BuildIdentityClient(RefreshResponse? refreshResponse)
    {
        var handler = new MockHttpMessageHandler(refreshResponse);
        var http    = new HttpClient(handler) { BaseAddress = new Uri("https://identity.test/") };
        return new IdentityClient(http, NullLogger<IdentityClient>.Instance);
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_StoresRefreshTokenInKeychain()
    {
        var keychain = new InMemoryCredentialStore();
        var sut      = BuildSut(keychain);

        await sut.HandleAuthCallbackAsync("refresh-token-123", CreateFakeJwt(DateTime.UtcNow.AddMinutes(15)));

        var stored = await keychain.GetAsync("ses-local-refresh");
        Assert.Equal("refresh-token-123", stored);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenCachedAndValid_ReturnsCachedToken()
    {
        var sut   = BuildSut();
        var token = CreateFakeJwt(DateTime.UtcNow.AddMinutes(15));

        await sut.HandleAuthCallbackAsync("refresh-token", token);

        var result = await sut.GetAccessTokenAsync();
        Assert.Equal(token, result);
    }

    [Fact]
    public async Task GetStateAsync_WhenNoTokensStored_ReturnsUnauthenticated()
    {
        var sut   = BuildSut();
        var state = await sut.GetStateAsync();

        Assert.False(state.IsAuthenticated);
        Assert.False(state.NeedsReauth);
    }

    [Fact]
    public async Task GetStateAsync_AfterSuccessfulCallback_ReturnsAuthenticated()
    {
        var sut   = BuildSut();
        var token = CreateFakeJwt(DateTime.UtcNow.AddMinutes(15));

        await sut.HandleAuthCallbackAsync("refresh", token);

        var state = await sut.GetStateAsync();
        Assert.True(state.IsAuthenticated);
        Assert.Equal(token, state.AccessToken);
    }

    [Fact]
    public async Task SignOutAsync_ClearsKeychain()
    {
        var keychain = new InMemoryCredentialStore();
        var sut      = BuildSut(keychain);

        await sut.HandleAuthCallbackAsync("refresh-token", CreateFakeJwt(DateTime.UtcNow.AddMinutes(15)));
        await sut.SignOutAsync();

        var state   = await sut.GetStateAsync();
        var refresh = await keychain.GetAsync("ses-local-refresh");
        Assert.False(state.IsAuthenticated);
        Assert.Null(refresh);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenExpired_RenewsViaRefreshToken()
    {
        var keychain = new InMemoryCredentialStore();
        await keychain.SetAsync("ses-local-refresh", "stored-refresh-token");

        var newAccess  = CreateFakeJwt(DateTime.UtcNow.AddMinutes(15));
        var newRefresh = "new-refresh-token";

        var refreshResponse = new RefreshResponse(
            newAccess, newRefresh,
            DateTime.UtcNow.AddMinutes(15),
            DateTime.UtcNow.AddDays(90));

        var identity = BuildIdentityClient(refreshResponse);
        var sut      = new AuthService(keychain, identity, NullLogger<AuthService>.Instance);

        // Don't call HandleAuthCallbackAsync — simulate already-stored-but-expired state
        // by getting token when _cachedAccessToken is null
        var result = await sut.GetAccessTokenAsync();

        Assert.Equal(newAccess, result);

        var storedRefresh = await keychain.GetAsync("ses-local-refresh");
        Assert.Equal(newRefresh, storedRefresh);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CreateFakeJwt(DateTime expiry)
    {
        var header  = Base64UrlEncode("""{"alg":"HS256","typ":"JWT"}""");
        var exp     = new DateTimeOffset(expiry).ToUnixTimeSeconds();
        var payload = Base64UrlEncode($$$"""{"sub":"user-123","exp":{{{exp}}},"iat":1234567890}""");
        return $"{header}.{payload}.fakesig";
    }

    private static string Base64UrlEncode(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private sealed class MockHttpMessageHandler(RefreshResponse? response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (response is null)
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));

            var json = System.Text.Json.JsonSerializer.Serialize(response,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
