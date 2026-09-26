using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IslaPay.Identity.Contracts;

namespace IslaPay.Identity;

/// <summary>A Keycloak account, as much of it as IslaPay cares about.</summary>
/// <param name="Attributes">
/// Keycloak stores attributes as lists of strings, whatever they mean. The
/// accessors below hide that rather than letting it leak into callers.
/// </param>
public sealed record KeycloakUser(
    string Id,
    string Username,
    string Email,
    string? FirstName,
    string? LastName,
    bool Enabled,
    bool EmailVerified,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes)
{
    public const string PhoneAttribute = "phoneNumber";
    public const string PhoneVerifiedAttribute = "phoneNumberVerified";

    // Compliance's marks. Admin-only in the realm's user profile, like the
    // phone's, so a person cannot unfreeze or verify themselves through the
    // account API.
    public const string IdentityVerifiedAttribute = "identityVerified";
    public const string FrozenAttribute = "frozen";
    public const string FrozenReasonAttribute = "frozenReason";
    public const string FrozenAtAttribute = "frozenAt";
    public const string FrozenByAttribute = "frozenBy";

    public static readonly IReadOnlyList<string> ComplianceAttributes =
    [
        IdentityVerifiedAttribute, FrozenAttribute, FrozenReasonAttribute, FrozenAtAttribute, FrozenByAttribute,
    ];

    public string? Phone => Attribute(PhoneAttribute);

    /// <summary>
    /// Absent counts as unverified. An account whose attribute never got
    /// written must not be treated as verified because of it.
    /// </summary>
    public bool PhoneVerified =>
        string.Equals(Attribute(PhoneVerifiedAttribute), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Absent counts as not checked, for the same reason.</summary>
    public bool IdentityVerified =>
        string.Equals(Attribute(IdentityVerifiedAttribute), "true", StringComparison.OrdinalIgnoreCase);

    public bool Frozen =>
        string.Equals(Attribute(FrozenAttribute), "true", StringComparison.OrdinalIgnoreCase);

    public string? FrozenReason => Attribute(FrozenReasonAttribute);

    public DateTimeOffset? FrozenAt =>
        DateTimeOffset.TryParse(Attribute(FrozenAtAttribute), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    public string? FrozenBy => Attribute(FrozenByAttribute);

    public string DisplayName =>
        string.Join(' ', new[] { FirstName, LastName }.Where(p => !string.IsNullOrWhiteSpace(p)))
            is { Length: > 0 } joined
            ? joined
            : Username;

    public string? Attribute(string name) =>
        Attributes.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
}

/// <summary>Administrative operations that a user's own token cannot perform.</summary>
public interface IAdminClient
{
    Task<KeycloakUser?> FindByEmailAsync(string email, CancellationToken ct = default);

    Task<KeycloakUser?> FindByPhoneAsync(string phone, CancellationToken ct = default);

    Task<KeycloakUser> GetAsync(string userId, CancellationToken ct = default);

    /// <returns>The new account's id.</returns>
    Task<string> CreateUserAsync(
        string email, string name, string phone, string password, CancellationToken ct = default);

    Task SetPasswordAsync(string userId, string password, CancellationToken ct = default);

    Task SetPhoneVerifiedAsync(string userId, bool verified, CancellationToken ct = default);

    /// <summary>Writes attributes; a null value removes one.</summary>
    Task SetAttributesAsync(
        string userId, IReadOnlyDictionary<string, string?> values, CancellationToken ct = default);
}

/// <summary>
/// Keycloak's admin REST API, through a service account.
/// </summary>
/// <remarks>
/// Everything here runs with <c>manage-users</c>, which is enough to take over
/// any account in the realm. Two consequences are built in: the surface is
/// kept to the six operations the identity flows actually need, and the
/// service-account token is cached in memory only, never written anywhere.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The semaphore is deliberately not disposed; see the note at the end of this type.")]
public sealed class KeycloakAdminClient : IAdminClient
{
    /// <summary>The named client this uses, configured by the module.</summary>
    public const string HttpClientName = "keycloak-admin";

    private readonly IHttpClientFactory _factory;
    private readonly KeycloakOptions _options;
    private readonly TimeProvider _clock;
    // Not disposed, and the analyser is told so below rather than silenced
    // globally: see the note where Dispose used to be.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA2213:Disposable fields should be disposed",
        Justification = "See the note at the end of this type.")]
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    /// <summary>
    /// Takes a factory rather than a client, because this is a singleton.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It has to be a singleton: the service-account token it caches is the
    /// whole point, and a new instance per call caches nothing — every
    /// administrative request would begin with a <c>client_credentials</c>
    /// round trip to Keycloak.
    /// </para>
    /// <para>
    /// A singleton must not hold an <see cref="HttpClient"/> for ever, though,
    /// because that pins the handler underneath it and defeats the pooling and
    /// DNS rotation the factory exists to provide. So: one client per call,
    /// from the factory, which is cheap.
    /// </para>
    /// </remarks>
    public KeycloakAdminClient(
        IHttpClientFactory factory, KeycloakOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        _factory = factory;
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    private HttpClient Http() => _factory.CreateClient(HttpClientName);

    public async Task<KeycloakUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        // exact=true, or Keycloak does an infix search and "ana@x.cu" would
        // also match "mariana@x.cu" — which would let a registration collide
        // with an unrelated account.
        var url = $"{_options.AdminBase}/users?email={Uri.EscapeDataString(email)}&exact=true";
        var users = await GetUsersAsync(url, ct).ConfigureAwait(false);
        return users.Count > 0 ? users[0] : null;
    }

    public async Task<KeycloakUser?> FindByPhoneAsync(string phone, CancellationToken ct = default)
    {
        var query = Uri.EscapeDataString($"{KeycloakUser.PhoneAttribute}:{phone}");
        var url = $"{_options.AdminBase}/users?q={query}&exact=true";
        var users = await GetUsersAsync(url, ct).ConfigureAwait(false);

        // The attribute query is a filter, not a guarantee; re-check rather
        // than trusting the server to have understood the syntax. Getting this
        // wrong would send someone else's OTP to this number.
        return users.FirstOrDefault(u =>
            string.Equals(u.Phone, phone, StringComparison.Ordinal));
    }

    public async Task<KeycloakUser> GetAsync(string userId, CancellationToken ct = default)
    {
        using var request = await AuthorizedAsync(
            HttpMethod.Get, $"{_options.AdminBase}/users/{Uri.EscapeDataString(userId)}", ct)
            .ConfigureAwait(false);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw IdentityException.TokenInvalid("The token names an account that no longer exists.");

        await EnsureSuccessAsync(response, "read a user", ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var json = JsonDocument.Parse(body);
        return ReadUser(json.RootElement);
    }

    public async Task<string> CreateUserAsync(
        string email, string name, string phone, string password, CancellationToken ct = default)
    {
        var (first, last) = SplitName(name);

        var payload = new
        {
            username = email,
            email,
            firstName = first,
            lastName = last,
            enabled = true,
            emailVerified = false,
            attributes = new Dictionary<string, string[]>
            {
                [KeycloakUser.PhoneAttribute] = [phone],
                [KeycloakUser.PhoneVerifiedAttribute] = ["false"],
            },
            credentials = new[]
            {
                new { type = "password", value = password, temporary = false },
            },
        };

        using var request = await AuthorizedAsync(
            HttpMethod.Post, $"{_options.AdminBase}/users", ct).ConfigureAwait(false);
        request.Content = JsonContent.Create(payload);

        using var response = await SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new IdentityException(
                IdentityErrors.EmailTaken, 409, "That e-mail already has an account.");
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var reason = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Keycloak reports a policy rejection as a 400 with the rule in the
            // body. Passing the rule through as detail is what lets the client
            // say something better than "invalid".
            throw new IdentityException(
                IdentityErrors.WeakPassword, 422, Summarise(reason));
        }

        await EnsureSuccessAsync(response, "create a user", ct).ConfigureAwait(false);

        // 201 with the id only in the Location header — there is no body.
        var location = response.Headers.Location?.ToString();
        var id = location?[(location.LastIndexOf('/') + 1)..];

        return string.IsNullOrEmpty(id)
            ? throw IdentityException.Unavailable("The account was created but its id was not returned.")
            : id;
    }

    public async Task SetPasswordAsync(string userId, string password, CancellationToken ct = default)
    {
        using var request = await AuthorizedAsync(
            HttpMethod.Put,
            $"{_options.AdminBase}/users/{Uri.EscapeDataString(userId)}/reset-password",
            ct).ConfigureAwait(false);
        request.Content = JsonContent.Create(new { type = "password", value = password, temporary = false });

        using var response = await SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var reason = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new IdentityException(IdentityErrors.WeakPassword, 422, Summarise(reason));
        }

        await EnsureSuccessAsync(response, "set a password", ct).ConfigureAwait(false);
    }

    public Task SetPhoneVerifiedAsync(string userId, bool verified, CancellationToken ct = default) =>
        SetAttributesAsync(
            userId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [KeycloakUser.PhoneVerifiedAttribute] = verified ? "true" : "false",
            },
            ct);

    public async Task SetAttributesAsync(
        string userId, IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        // Read first, then write the whole representation back.
        //
        // A PUT to /users is validated against the realm's declarative user
        // profile, and anything the body omits is treated as removed. Sending
        // only the attribute map therefore drops the name and e-mail, and
        // Keycloak responds by adding a VERIFY_PROFILE required action — after
        // which the account cannot sign in at all and the error says "account
        // is not fully set up", which points nowhere near this method.
        var current = await GetAsync(userId, ct).ConfigureAwait(false);

        var attributes = current.Attributes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (value is null) attributes.Remove(name);
            else attributes[name] = [value];
        }

        using var request = await AuthorizedAsync(
            HttpMethod.Put, $"{_options.AdminBase}/users/{Uri.EscapeDataString(userId)}", ct)
            .ConfigureAwait(false);
        request.Content = JsonContent.Create(new
        {
            username = current.Username,
            email = current.Email,
            firstName = current.FirstName,
            lastName = current.LastName,
            enabled = current.Enabled,
            emailVerified = current.EmailVerified,
            attributes,
        });

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "update a user", ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ plumbing

    private async Task<IReadOnlyList<KeycloakUser>> GetUsersAsync(string url, CancellationToken ct)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "search users", ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var json = JsonDocument.Parse(body);

        return [.. json.RootElement.EnumerateArray().Select(ReadUser)];
    }

    private static KeycloakUser ReadUser(JsonElement element)
    {
        var attributes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (element.TryGetProperty("attributes", out var attrs)
            && attrs.ValueKind == JsonValueKind.Object)
        {
            foreach (var attribute in attrs.EnumerateObject())
            {
                attributes[attribute.Name] = attribute.Value.ValueKind == JsonValueKind.Array
                    ? [.. attribute.Value.EnumerateArray().Select(v => v.GetString() ?? string.Empty)]
                    : [attribute.Value.GetString() ?? string.Empty];
            }
        }

        return new KeycloakUser(
            Id: Text(element, "id"),
            Username: Text(element, "username"),
            Email: Text(element, "email"),
            FirstName: Text(element, "firstName"),
            LastName: Text(element, "lastName"),
            Enabled: Flag(element, "enabled"),
            EmailVerified: Flag(element, "emailVerified"),
            Attributes: attributes);

        static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v.GetString() ?? string.Empty : string.Empty;

        static bool Flag(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }

    private async Task<HttpRequestMessage> AuthorizedAsync(
        HttpMethod method, string url, CancellationToken ct)
    {
        var token = await ServiceAccountTokenAsync(ct).ConfigureAwait(false);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<string> ServiceAccountTokenAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_token is not null && now < _tokenExpiresAt) return _token;

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = _clock.GetUtcNow();
            if (_token is not null && now < _tokenExpiresAt) return _token;

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.AdminClientId,
                ["client_secret"] = _options.AdminClientSecret,
            };

            using var http = Http();
            using var response = await http
                .PostAsync(_options.TokenEndpoint, new FormUrlEncodedContent(form), ct)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw IdentityException.Unavailable(
                    $"The admin service account could not authenticate ({(int)response.StatusCode}).");
            }

            using var json = JsonDocument.Parse(body);
            var token = json.RootElement.TryGetProperty("access_token", out var t)
                ? t.GetString()
                : null;

            if (string.IsNullOrEmpty(token))
                throw IdentityException.Unavailable("The admin token response carried no token.");

            var lifetime = json.RootElement.TryGetProperty("expires_in", out var e)
                ? TimeSpan.FromSeconds(e.GetInt32())
                : TimeSpan.FromSeconds(60);

            _token = token;
            _tokenExpiresAt = now + lifetime - _options.AdminTokenRenewalMargin;
            return token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var http = Http();
            return await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw IdentityException.Unavailable("The identity provider did not answer.", e);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw IdentityException.Unavailable(
            $"Could not {what}: the provider answered {(int)response.StatusCode}. {Summarise(body)}");
    }

    /// <summary>Keeps a provider error out of the logs at full length.</summary>
    private static string Summarise(string body) =>
        body.Length <= 200 ? body : body[..200] + "…";

    private static (string First, string Last) SplitName(string name)
    {
        var trimmed = name.Trim();
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (trimmed, string.Empty) : (trimmed[..space], trimmed[(space + 1)..].Trim());
    }

    // No Dispose, deliberately.
    //
    // SemaphoreSlim only needs disposing if its AvailableWaitHandle was used,
    // and it is not. Disposing it was actively harmful: the container disposes
    // this on shutdown, and anything still waiting on the gate — a request
    // finishing, a readiness probe — then throws ObjectDisposedException for a
    // reason that has nothing to do with what it was doing. That was a test
    // failing roughly one run in three, reported as "cannot access a disposed
    // object" with no hint of where it came from.
}
