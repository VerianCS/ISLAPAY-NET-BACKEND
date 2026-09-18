namespace IslaPay.Identity;

/// <summary>Where Keycloak lives and which client we speak as.</summary>
/// <remarks>
/// Two clients, not one, and the separation is the point. The public client
/// carries the user's own grant and can do nothing an authenticated user
/// cannot. The confidential client holds a service-account secret with
/// <c>manage-users</c> — it can create accounts and set passwords, so it is
/// used only by the handful of calls that genuinely need it and its secret
/// never leaves the server.
/// </remarks>
public sealed class KeycloakOptions
{
    /// <summary>Base URL, without a trailing slash, e.g. <c>http://localhost:8080</c>.</summary>
    public string Authority { get; init; } = "http://localhost:8080";

    /// <summary>The realm IslaPay's users live in. Never <c>master</c>.</summary>
    public string Realm { get; init; } = "islapay";

    /// <summary>The public client the mobile app authenticates through.</summary>
    public string ClientId { get; init; } = "islapay-app";

    /// <summary>
    /// Set only if the app client is confidential. A mobile app cannot keep a
    /// secret, so in production this is normally empty and the client is
    /// public with direct grant enabled.
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>The confidential client used for administrative calls.</summary>
    public string AdminClientId { get; init; } = "islapay-admin";

    /// <summary>Its service-account secret. Comes from the secret store, never from source.</summary>
    public string AdminClientSecret { get; init; } = string.Empty;

    /// <summary>
    /// The audience an access token must name for the API to accept it.
    /// </summary>
    /// <remarks>
    /// Keycloak does not put a useful <c>aud</c> in an access token unless a
    /// mapper is configured to, and the realm may host clients that have
    /// nothing to do with IslaPay. Without this check, a token minted for any
    /// of them opens this API. The realm setup adds a hardcoded audience
    /// mapper to the app client so that this value appears.
    /// </remarks>
    public string Audience { get; init; } = "islapay-api";

    /// <summary>
    /// How long before expiry to renew the admin service-account token.
    /// Renewing early costs one extra token request; renewing late means an
    /// administrative call fails for a reason that has nothing to do with it.
    /// </summary>
    public TimeSpan AdminTokenRenewalMargin { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Issuer the access tokens must carry — what the API validates against.</summary>
    public string Issuer => $"{Authority.TrimEnd('/')}/realms/{Realm}";

    /// <summary>OpenID Connect discovery document.</summary>
    public string MetadataAddress => $"{Issuer}/.well-known/openid-configuration";

    internal string TokenEndpoint => $"{Issuer}/protocol/openid-connect/token";

    internal string LogoutEndpoint => $"{Issuer}/protocol/openid-connect/logout";

    internal string AdminBase => $"{Authority.TrimEnd('/')}/admin/realms/{Realm}";
}
