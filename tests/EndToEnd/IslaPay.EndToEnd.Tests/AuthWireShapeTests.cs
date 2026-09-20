using System.Net.Http.Json;
using System.Text.Json;
using IslaPay.Identity.Contracts;
using IslaPay.Platform.Serialization;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// The exact JSON the Flutter client parses.
/// </summary>
/// <remarks>
/// <para>
/// Every other test here deserialises into the server's own record types,
/// which means a property could be renamed and every one of them would still
/// pass while the app stopped being able to sign anybody in. The client has no
/// share in these types: it reads field names out of a document.
/// </para>
/// <para>
/// So this reads the raw document and names the fields. It is deliberately
/// dull, and it is the only thing standing between a rename here and a silent
/// break over there. If one of these assertions fails, the fix is either to
/// put the field back or to change the client and `API_CONTRACT.md` with it.
/// </para>
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class AuthWireShapeTests
{
    private const string Password = "Correct-Horse-9";
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    private readonly IslaPayHostFixture _fixture;

    public AuthWireShapeTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Register_answers_with_the_document_the_app_reads()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        using var client = host.CreateClient();

        var email = $"shape-{Guid.NewGuid():N}@example.test";
        var response = await client.PostAsJsonAsync(
            "/v1/auth/register",
            new RegisterRequest("Ana Pérez", email, "+5355123456", Password),
            Json);

        Assert.True(response.IsSuccessStatusCode,
            $"register returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // Session.fromAuthResponse in the app refuses anything else.
        var tokens = root.GetProperty("tokens");
        var user = root.GetProperty("user");

        // TokenPair.fromJson. `expiresIn` has to be a number of seconds: the
        // app turns it into an instant, and a string or an absolute timestamp
        // here would be read as a lifetime of a few hundred milliseconds.
        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("refreshToken").GetString()));
        Assert.Equal(JsonValueKind.Number, tokens.GetProperty("expiresIn").ValueKind);
        Assert.Equal(JsonValueKind.Number, tokens.GetProperty("refreshExpiresIn").ValueKind);
        Assert.True(tokens.GetProperty("expiresIn").GetInt32() > 0);
        Assert.Equal("Bearer", tokens.GetProperty("tokenType").GetString());

        // SessionUser.fromJson.
        Assert.False(string.IsNullOrEmpty(user.GetProperty("id").GetString()));
        Assert.Equal("Ana Pérez", user.GetProperty("name").GetString());
        Assert.Equal(email, user.GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.False, user.GetProperty("phoneVerified").ValueKind);
    }

    [SkippableFact]
    public async Task Refresh_answers_with_a_bare_pair_and_not_a_session()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        using var client = host.CreateClient();

        var registered = await client.PostAsJsonAsync(
            "/v1/auth/register",
            new RegisterRequest(
                "Ana Pérez", $"shape-{Guid.NewGuid():N}@example.test", "+5355123457", Password),
            Json);

        var session = await registered.Content.ReadFromJsonAsync<AuthSessionResponse>(Json);

        var refreshed = await client.PostAsJsonAsync(
            "/v1/auth/token/refresh",
            new RefreshRequest(session!.Tokens.RefreshToken),
            Json);

        Assert.True(refreshed.IsSuccessStatusCode,
            $"refresh returned {(int)refreshed.StatusCode}: {await refreshed.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // The app's AuthClient.refresh reads the pair from the root. Wrapping
        // it in a session later would break it without failing anything else.
        Assert.False(string.IsNullOrEmpty(root.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("refreshToken").GetString()));
        Assert.Equal(JsonValueKind.Number, root.GetProperty("expiresIn").ValueKind);
        Assert.False(root.TryGetProperty("user", out _));
    }

    [SkippableFact]
    public async Task A_refused_sign_in_carries_the_code_the_app_switches_on()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        using var client = host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/auth/login",
            new LoginRequest($"nobody-{Guid.NewGuid():N}@example.test", "whatever"),
            Json);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // AuthFailure.fromProblem maps on `code` and nothing else. The title
        // and detail are free to change and to be translated; this is not.
        Assert.Equal(
            IdentityErrors.InvalidCredentials,
            document.RootElement.GetProperty("code").GetString());
    }
}
