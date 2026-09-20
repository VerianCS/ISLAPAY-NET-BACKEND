using System.Net;
using System.Text.Json;
using IslaPay.Identity.Contracts;

namespace IslaPay.Identity;

/// <summary>Exchanges credentials and refresh tokens for token pairs.</summary>
public interface ITokenClient
{
    Task<TokenPair> PasswordGrantAsync(string username, string password, CancellationToken ct = default);

    Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Revokes a refresh token at the provider.</summary>
    Task LogoutAsync(string refreshToken, CancellationToken ct = default);
}

/// <summary>
/// Keycloak's token endpoint, and nothing else.
/// </summary>
/// <remarks>
/// This class is the only place a user's plaintext password exists on the
/// server, for the duration of one call. It is never logged, never stored and
/// never put in an exception message — which is why the error mapping below
/// reads Keycloak's <c>error_description</c> but never echoes the request.
/// </remarks>
public sealed class KeycloakTokenClient : ITokenClient
{
    private readonly HttpClient _http;
    private readonly KeycloakOptions _options;

    public KeycloakTokenClient(HttpClient http, KeycloakOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options;
    }

    public Task<TokenPair> PasswordGrantAsync(
        string username, string password, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = _options.ClientId,
            ["username"] = username,
            ["password"] = password,
            // openid, so the response carries an id token's claims and the
            // access token is a proper OIDC token rather than a bare JWT.
            ["scope"] = "openid profile email",
        };
        if (!string.IsNullOrEmpty(_options.ClientSecret))
            form["client_secret"] = _options.ClientSecret;

        return PostForTokensAsync(form, isRefresh: false, ct);
    }

    public Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _options.ClientId,
            ["refresh_token"] = refreshToken,
        };
        if (!string.IsNullOrEmpty(_options.ClientSecret))
            form["client_secret"] = _options.ClientSecret;

        return PostForTokensAsync(form, isRefresh: true, ct);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["refresh_token"] = refreshToken,
        };
        if (!string.IsNullOrEmpty(_options.ClientSecret))
            form["client_secret"] = _options.ClientSecret;

        // A logout that fails is not worth failing the request over: the token
        // expires by itself, and the client has already discarded it. It is
        // worth attempting, because until it succeeds a stolen refresh token
        // stays usable for its full lifetime.
        try
        {
            using var response = await _http
                .PostAsync(_options.LogoutEndpoint, new FormUrlEncodedContent(form), ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Swallowed on purpose — see above.
        }
    }

    private async Task<TokenPair> PostForTokensAsync(
        Dictionary<string, string> form, bool isRefresh, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http
                .PostAsync(_options.TokenEndpoint, new FormUrlEncodedContent(form), ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw IdentityException.Unavailable("The identity provider did not answer.", e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw Translate(response.StatusCode, body, isRefresh);

            return Parse(body);
        }
    }

    private static TokenPair Parse(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        // Required by RFC 6749. If any is missing the provider is not speaking
        // OAuth 2 and guessing a default would hide that.
        var access = Require(root, "access_token");
        var refresh = Require(root, "refresh_token");

        return new TokenPair(
            AccessToken: access,
            RefreshToken: refresh,
            ExpiresIn: root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 0,
            RefreshExpiresIn: root.TryGetProperty("refresh_expires_in", out var r) ? r.GetInt32() : 0,
            TokenType: root.TryGetProperty("token_type", out var t)
                ? t.GetString() ?? "Bearer"
                : "Bearer");

        static string Require(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } s
                ? s
                : throw IdentityException.Unavailable(
                    $"The token response had no usable '{name}'.");
    }

    /// <summary>
    /// Turns Keycloak's OAuth error into one of ours.
    /// </summary>
    /// <remarks>
    /// The provider answers a wrong password and a disabled account with the
    /// same <c>invalid_grant</c>, distinguished only by the description text.
    /// Matching on prose is unpleasant and it is what the protocol leaves us,
    /// so it is contained here: everything unrecognised falls back to
    /// <see cref="IdentityErrors.InvalidCredentials"/>, which is the safe answer —
    /// it tells an attacker nothing and tells an honest user to try again.
    /// </remarks>
    private static IdentityException Translate(HttpStatusCode status, string body, bool isRefresh)
    {
        var (error, description) = ReadOAuthError(body);

        if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            if (isRefresh)
                return IdentityException.TokenInvalid(
                    $"The refresh token was rejected: {error}.");

            if (description.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                || description.Contains("not fully set up", StringComparison.OrdinalIgnoreCase))
            {
                return new IdentityException(
                    IdentityErrors.AccountDisabled, 403, "The account is not usable: " + description);
            }

            return IdentityException.InvalidCredentials();
        }

        // Keycloak's own brute-force protection answers 429 here. Passing it
        // through unchanged is right: the client already honours Retry-After.
        if (status == HttpStatusCode.TooManyRequests)
        {
            return new IdentityException(
                IdentityErrors.TooManyAttempts, 429, "The provider is rate-limiting this account.");
        }

        return IdentityException.Unavailable(
            $"The identity provider answered {(int)status}: {error}.");
    }

    private static (string Error, string Description) ReadOAuthError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            return (
                root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "",
                root.TryGetProperty("error_description", out var d) ? d.GetString() ?? "" : "");
        }
        catch (JsonException)
        {
            return ("unparseable", "");
        }
    }
}
