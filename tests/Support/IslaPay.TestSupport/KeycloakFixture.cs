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
        await NameTheSignInStepsAsync();
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
    /// <summary>As <c>KeycloakUser.ComplianceAttributes</c>, which this project cannot see.</summary>
    private static readonly string[] ComplianceAttributes =
        ["identityVerified", "frozen", "frozenReason", "frozenAt", "frozenBy"];

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
                // Compliance's marks, admin-only for the same reason.
                .Concat(ComplianceAttributes.Select(name => (object)new
                {
                    name,
                    displayName = name,
                    multivalued = false,
                    adminOnly.permissions,
                }))
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
                // How the person signed in, so the API can tell a password
                // from a password and a code. As tools/provision-realm.py does.
                new
                {
                    name = "amr",
                    protocol = "openid-connect",
                    protocolMapper = "oidc-amr-mapper",
                    config = new Dictionary<string, string>
                    {
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
    /// Gives the direct-grant steps the names the <c>amr</c> claim reports.
    /// </summary>
    /// <remarks>
    /// Without a reference a step is performed and never mentioned, and the
    /// token says nothing about how anybody signed in.
    /// </remarks>
    private async Task NameTheSignInStepsAsync()
    {
        using var read = Authorized(
            HttpMethod.Get, $"{Authority}/admin/realms/{Realm}/authentication/flows/direct%20grant/executions");
        using var found = await _http.SendAsync(read);
        found.EnsureSuccessStatusCode();

        var references = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["direct-grant-validate-password"] = "pwd",
            ["direct-grant-validate-otp"] = "otp",
        };

        foreach (var step in (await found.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray())
        {
            if (!step.TryGetProperty("providerId", out var provider)
                || !references.TryGetValue(provider.GetString() ?? "", out var reference))
            {
                continue;
            }

            using var configure = Authorized(
                HttpMethod.Post,
                $"{Authority}/admin/realms/{Realm}/authentication/executions/{step.GetProperty("id").GetString()}/config");
            configure.Content = JsonContent.Create(new
            {
                alias = $"amr-{reference}",
                config = new Dictionary<string, string> { ["default.reference.value"] = reference },
            });
            using var configured = await _http.SendAsync(configure);
            await Expect(configured, HttpStatusCode.Created, $"name the {reference} step");
        }
    }

    /// <summary>
    /// A member of staff with a password and an authenticator app, and the
    /// app's secret so a test can compute the codes it would show.
    /// </summary>
    /// <remarks>
    /// Imported rather than registered: Keycloak's admin API cannot add an
    /// authenticator to an existing user, because in real life the person
    /// scans a QR code. An import is the one door that takes the secret.
    /// </remarks>
    public async Task<(string UserId, byte[] Secret)> StaffWithAuthenticatorAsync(
        string email, string password, params string[] roles)
    {
        _adminToken = await MasterTokenAsync();

        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
        // Keycloak keeps the secret as text and uses its bytes as the key, so
        // the key has to be printable.
        var key = Convert.ToHexString(secret);

        using (var import = Authorized(HttpMethod.Post, $"{Authority}/admin/realms/{Realm}/partialImport"))
        {
            import.Content = JsonContent.Create(new
            {
                ifResourceExists = "FAIL",
                users = new[]
                {
                    new
                    {
                        username = email, email, firstName = "Staff", lastName = "Member",
                        enabled = true, emailVerified = true,
                        credentials = new object[]
                        {
                            new { type = "password", value = password, temporary = false },
                            new
                            {
                                type = "otp", userLabel = "test",
                                secretData = JsonSerializer.Serialize(new { value = key }),
                                credentialData = JsonSerializer.Serialize(new
                                {
                                    subType = "totp", digits = 6, period = 30,
                                    algorithm = "HmacSHA1", counter = 0,
                                }),
                            },
                        },
                    },
                },
            });
            using var imported = await _http.SendAsync(import);
            await Expect(imported, HttpStatusCode.OK, "import a member of staff");
        }

        using var find = Authorized(
            HttpMethod.Get, $"{Authority}/admin/realms/{Realm}/users?exact=true&email={Uri.EscapeDataString(email)}");
        using var users = await _http.SendAsync(find);
        users.EnsureSuccessStatusCode();
        var id = (await users.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetString()!;

        foreach (var role in roles)
            await GrantRealmRoleAsync(id, role);

        return (id, System.Text.Encoding.ASCII.GetBytes(key));
    }

    /// <summary>The code an authenticator app would show now (RFC 6238, SHA-1, six digits).</summary>
    public static string CurrentCode(byte[] secret)
    {
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);

#pragma warning disable CA5350 // TOTP is defined over HMAC-SHA1; this is the authenticator's arithmetic, not a choice.
        var hash = System.Security.Cryptography.HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
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

    /// <summary>
    /// Creates a realm role if it does not exist and gives it to a user.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A realm role, not a client one, because that is what the modules look
    /// for: a role tied to one client would have to be re-granted the day the
    /// API is split in two, and an operator console added later would not
    /// inherit it.
    /// </para>
    /// <para>
    /// The caller must get a token <em>after</em> this runs. Roles are baked
    /// into an access token when it is issued, so one minted a moment earlier
    /// will keep saying no until it expires.
    /// </para>
    /// </remarks>
    public async Task GrantRealmRoleAsync(string userId, string role)
    {
        // Re-read: the master token is short-lived and this runs whenever a
        // test needs it rather than during set-up.
        _adminToken = await MasterTokenAsync();

        using (var create = Authorized(HttpMethod.Post, $"{Authority}/admin/realms/{Realm}/roles"))
        {
            create.Content = JsonContent.Create(new { name = role });
            using var created = await _http.SendAsync(create);

            // Conflict means another test made it first, which is the answer
            // this method wanted anyway.
            if (created.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.Conflict))
            {
                await Expect(created, HttpStatusCode.Created, $"create the realm role {role}");
            }
        }

        string roleId;
        using (var read = Authorized(
            HttpMethod.Get, $"{Authority}/admin/realms/{Realm}/roles/{Uri.EscapeDataString(role)}"))
        {
            using var found = await _http.SendAsync(read);
            found.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await found.Content.ReadAsStringAsync());
            roleId = json.RootElement.GetProperty("id").GetString()!;
        }

        using var map = Authorized(
            HttpMethod.Post,
            $"{Authority}/admin/realms/{Realm}/users/{userId}/role-mappings/realm");
        map.Content = JsonContent.Create(new[] { new { id = roleId, name = role } });

        using var mapped = await _http.SendAsync(map);
        await Expect(mapped, HttpStatusCode.NoContent, $"grant {role} to {userId}");
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
