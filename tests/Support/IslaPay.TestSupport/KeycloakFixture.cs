using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace IslaPay.TestSupport;

/// <summary>
/// Builds a realm in a live Keycloak and tears it down afterwards.
/// </summary>
/// <remarks>
/// <para>
/// The realm is created through the same Admin API the production code uses,
/// rather than imported from a JSON file. That is the point: an import would
/// prove that a file parses, while this proves that the calls we make are
/// accepted by the version of Keycloak we are running against — including the
/// user-profile configuration, which is where a phone attribute silently
/// disappears if it is not declared.
/// </para>
/// <para>
/// Every run gets its own realm name, so two runs on one broker (a developer's
/// machine while CI is also pointed at it) cannot see each other's users.
/// </para>
/// </remarks>
public sealed class KeycloakFixture : IAsyncLifetime, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private string? _adminToken;

    public string Authority { get; } =
        Environment.GetEnvironmentVariable("KEYCLOAK_URL") ?? "http://localhost:8080";

    public string Realm { get; } = $"islapay-test-{Guid.NewGuid():N}"[..24];

    public string AdminClientSecret { get; } = Guid.NewGuid().ToString("N");

    public bool Available { get; private set; }

    /// <summary>
    /// The user attributes the Identity module stores, declared here because
    /// the realm's user profile has to know about them before anything can
    /// write one. Kept in step with <c>KeycloakUser</c> by the tests that
    /// exercise both.
    /// </summary>
    public const string PhoneAttribute = "phoneNumber";
    public const string PhoneVerifiedAttribute = "phoneNumberVerified";

    /// <summary>Another client in the realm, used to prove the audience check bites.</summary>
    public const string StrangerClientId = "some-other-app";

    public const string AppClientId = "islapay-app";
    public const string AdminClientId = "islapay-admin";
    public const string Audience = "islapay-api";

    /// <summary>
    /// The configuration a host needs to talk to this realm.
    /// </summary>
    /// <remarks>
    /// Plain settings rather than the Identity module's options type: this
    /// project is shared test infrastructure and must not depend on a module,
    /// or every other module's tests inherit Identity through the back door.
    /// </remarks>
    public IReadOnlyDictionary<string, string> HostSettings => new Dictionary<string, string>(
        StringComparer.Ordinal)
    {
        ["Keycloak:Authority"] = Authority,
        ["Keycloak:Realm"] = Realm,
        ["Keycloak:ClientId"] = AppClientId,
        ["Keycloak:AdminClientId"] = AdminClientId,
        ["Keycloak:AdminClientSecret"] = AdminClientSecret,
        ["Keycloak:Audience"] = Audience,
    };

    public async Task InitializeAsync()
    {
        try
        {
            _adminToken = await MasterTokenAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Available = false;
            return;
        }

        await CreateRealmAsync();
        await ConfigureUserProfileAsync();
        await CreateAppClientAsync();
        await CreateStrangerClientAsync();
        await CreateAdminClientAsync();
        Available = true;
    }

    public async Task DisposeAsync()
    {
        if (Available && _adminToken is not null)
        {
            using var request = Authorized(HttpMethod.Delete, $"{Authority}/admin/realms/{Realm}");
            try
            {
                using var _ = await _http.SendAsync(request);
            }
            catch (HttpRequestException)
            {
                // A leftover realm on a developer's machine is untidy, not a
                // test failure.
            }
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Signs in as the bootstrap administrator of the master realm.</summary>
    private async Task<string> MasterTokenAsync()
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = Environment.GetEnvironmentVariable("KEYCLOAK_ADMIN") ?? "admin",
            ["password"] = Environment.GetEnvironmentVariable("KEYCLOAK_ADMIN_PASSWORD") ?? "admin",
        };

        using var response = await _http.PostAsync(
            $"{Authority}/realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(form));

        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task CreateRealmAsync()
    {
        using var request = Authorized(HttpMethod.Post, $"{Authority}/admin/realms");
        request.Content = JsonContent.Create(new
        {
            realm = Realm,
            enabled = true,
            // So the weak-password path has something to reject. Without a
            // policy Keycloak accepts "1", and the test would prove nothing.
            passwordPolicy = "length(8)",
            // Short, so a test can watch an access token expire without
            // waiting five minutes for it.
            accessTokenLifespan = 60,
        });

        using var response = await _http.SendAsync(request);
        await Expect(response, HttpStatusCode.Created, "create the realm");
    }

    /// <summary>
    /// Declares the phone attributes on the realm's user profile.
    /// </summary>
    /// <remarks>
    /// Keycloak 24 onwards rejects or drops attributes the user profile does
    /// not know about. Declaring them — rather than switching unmanaged
    /// attributes on wholesale — also lets the permissions be set correctly:
    /// both are admin-readable and admin-writable only, so a user cannot set
    /// their own <c>phoneNumberVerified</c> to true through the account API.
    /// </remarks>
    private async Task ConfigureUserProfileAsync()
    {
        using var read = Authorized(HttpMethod.Get, $"{Authority}/admin/realms/{Realm}/users/profile");
        using var current = await _http.SendAsync(read);
        current.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await current.Content.ReadAsStringAsync());

        var attributes = json.RootElement.GetProperty("attributes")
            .EnumerateArray()
            .Select(a => JsonSerializer.Deserialize<JsonElement>(a.GetRawText()))
            .ToList();

        var adminOnly = new
        {
            permissions = new { view = new[] { "admin" }, edit = new[] { "admin" } },
        };

        var profile = new Dictionary<string, object>
        {
            ["attributes"] = attributes
                .Select(a => (object)a)
                .Concat(
                [
                    new
                    {
                        name = PhoneAttribute,
                        displayName = "Phone",
                        multivalued = false,
                        adminOnly.permissions,
                    },
                    new
                    {
                        name = PhoneVerifiedAttribute,
                        displayName = "Phone verified",
                        multivalued = false,
                        adminOnly.permissions,
                    },
                ])
                .ToArray(),
        };

        if (json.RootElement.TryGetProperty("groups", out var groups))
            profile["groups"] = JsonSerializer.Deserialize<JsonElement>(groups.GetRawText());

        using var write = Authorized(HttpMethod.Put, $"{Authority}/admin/realms/{Realm}/users/profile");
        write.Content = JsonContent.Create(profile);

        using var response = await _http.SendAsync(write);
        await Expect(response, HttpStatusCode.OK, "configure the user profile");
    }

    /// <summary>The public client the app authenticates through.</summary>
    private async Task CreateAppClientAsync()
    {
        using var request = Authorized(HttpMethod.Post, $"{Authority}/admin/realms/{Realm}/clients");
        request.Content = JsonContent.Create(new
        {
            clientId = "islapay-app",
            enabled = true,
            // Public: a mobile app cannot keep a secret, and pretending it can
            // is worse than admitting it cannot.
            publicClient = true,
            // Direct grant, because D8 keeps the native screens. Standard flow
            // is off: nothing should be able to start a browser redirect
            // against this client.
            directAccessGrantsEnabled = true,
            standardFlowEnabled = false,
            serviceAccountsEnabled = false,
            protocolMappers = new[]
            {
                new
                {
                    name = "islapay-audience",
                    protocol = "openid-connect",
                    protocolMapper = "oidc-audience-mapper",
                    config = new Dictionary<string, string>
                    {
                        ["included.custom.audience"] = "islapay-api",
                        ["access.token.claim"] = "true",
                        ["id.token.claim"] = "false",
                    },
                },
            },
        });

        using var response = await _http.SendAsync(request);
        await Expect(response, HttpStatusCode.Created, "create the app client");
    }

    /// <summary>
    /// A second public client in the same realm, with no audience mapper.
    /// </summary>
    /// <remarks>
    /// Exists so a test can hold a token that Keycloak itself considers valid —
    /// right realm, right signature, unexpired — and confirm the API still
    /// refuses it. That is the whole reason the audience is checked: a realm
    /// is shared, and "signed by our issuer" is not the same as "meant for
    /// us".
    /// </remarks>
    private async Task CreateStrangerClientAsync()
    {
        using var request = Authorized(HttpMethod.Post, $"{Authority}/admin/realms/{Realm}/clients");
        request.Content = JsonContent.Create(new
        {
            clientId = StrangerClientId,
            enabled = true,
            publicClient = true,
            directAccessGrantsEnabled = true,
            standardFlowEnabled = false,
            serviceAccountsEnabled = false,
        });

        using var response = await _http.SendAsync(request);
        await Expect(response, HttpStatusCode.Created, "create the stranger client");
    }

    /// <summary>Signs in through the client that has no IslaPay audience.</summary>
    public async Task<string> StrangerTokenAsync(string username, string password)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = StrangerClientId,
            ["username"] = username,
            ["password"] = password,
            ["scope"] = "openid",
        };

        using var response = await _http.PostAsync(
            $"{Authority}/realms/{Realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(form));

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stranger sign-in failed: {body}");

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>The confidential client whose service account may manage users.</summary>
    private async Task CreateAdminClientAsync()
    {
        using var create = Authorized(HttpMethod.Post, $"{Authority}/admin/realms/{Realm}/clients");
        create.Content = JsonContent.Create(new
        {
            clientId = "islapay-admin",
            enabled = true,
            publicClient = false,
            secret = AdminClientSecret,
            serviceAccountsEnabled = true,
            standardFlowEnabled = false,
            directAccessGrantsEnabled = false,
        });

        using (var response = await _http.SendAsync(create))
            await Expect(response, HttpStatusCode.Created, "create the admin client");

        var clientUuid = await ClientUuidAsync("islapay-admin");
        var serviceAccount = await ServiceAccountUserIdAsync(clientUuid);
        var realmManagement = await ClientUuidAsync("realm-management");

        // manage-users and view-users, and nothing else. The service account
        // can create an account and reset a password; it cannot touch realm
        // settings, clients or roles.
        var roles = await RolesAsync(realmManagement, "manage-users", "view-users");

        using var grant = Authorized(
            HttpMethod.Post,
            $"{Authority}/admin/realms/{Realm}/users/{serviceAccount}/role-mappings/clients/{realmManagement}");
        grant.Content = JsonContent.Create(roles);

        using var granted = await _http.SendAsync(grant);
        await Expect(granted, HttpStatusCode.NoContent, "grant the service account its roles");
    }

    private async Task<string> ClientUuidAsync(string clientId)
    {
        using var request = Authorized(
            HttpMethod.Get,
            $"{Authority}/admin/realms/{Realm}/clients?clientId={Uri.EscapeDataString(clientId)}");
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement[0].GetProperty("id").GetString()!;
    }

    private async Task<string> ServiceAccountUserIdAsync(string clientUuid)
    {
        using var request = Authorized(
            HttpMethod.Get,
            $"{Authority}/admin/realms/{Realm}/clients/{clientUuid}/service-account-user");
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<object[]> RolesAsync(string clientUuid, params string[] names)
    {
        var roles = new List<object>(names.Length);
        foreach (var name in names)
        {
            using var request = Authorized(
                HttpMethod.Get,
                $"{Authority}/admin/realms/{Realm}/clients/{clientUuid}/roles/{name}");
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            roles.Add(new
            {
                id = json.RootElement.GetProperty("id").GetString(),
                name = json.RootElement.GetProperty("name").GetString(),
            });
        }

        return [.. roles];
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        return request;
    }

    private static async Task Expect(HttpResponseMessage response, HttpStatusCode expected, string what)
    {
        if (response.StatusCode == expected) return;

        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(
            $"Could not {what}: {(int)response.StatusCode}. {body}");
    }
}

/// <summary>
/// One realm for the whole suite. Bootstrapping takes a second or two and
/// nothing in these tests needs isolation beyond a unique e-mail per case.
/// </summary>
[CollectionDefinition(Name)]
public sealed class KeycloakRealmDefinition : ICollectionFixture<KeycloakFixture>
{
    public const string Name = "keycloak";
}
