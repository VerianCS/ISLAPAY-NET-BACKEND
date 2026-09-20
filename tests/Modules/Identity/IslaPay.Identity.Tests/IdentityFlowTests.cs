using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IslaPay.Identity;
using IslaPay.Identity.Contracts;
using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Serialization;
using IslaPay.TestSupport;

namespace IslaPay.Identity.Tests;

/// <summary>
/// The identity flows, end to end, against a running Keycloak.
/// </summary>
/// <remarks>
/// <para>
/// Every request here goes through the real host: the bearer middleware, the
/// problem-details writer and the Keycloak clients are the ones that ship. The
/// only substitution is the SMS sender, because a test cannot read a text
/// message.
/// </para>
/// <para>
/// Skipped rather than failed when no Keycloak is reachable, so a developer
/// without one running is not shown red for someone else's infrastructure. CI
/// runs one, and a skip there would be the silent failure worth guarding
/// against — which is why the CI job asserts on the count.
/// </para>
/// </remarks>
[Collection(IdentityHostDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class IdentityFlowTests : IDisposable
{
    private const string Password = "Correct-Horse-9";

    private readonly IdentityHostFixture _host;
    private readonly IslaPayApiFactory? _factory;

    public IdentityFlowTests(IdentityHostFixture host)
    {
        _host = host;
        _factory = host.Available ? new IslaPayApiFactory(host.HostSettings) : null;
    }

    public void Dispose() => _factory?.Dispose();

    // ------------------------------------------------------------------ register

    [SkippableFact]
    public async Task Registering_signs_the_user_in_with_the_phone_still_unverified()
    {
        var (client, _) = Ready();
        var account = NewAccount();

        var response = await client.PostAsJsonAsync("/v1/auth/register", account, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await Read<AuthSessionResponse>(response);

        Assert.Equal(account.Email, session.User.Email);
        Assert.Equal(account.Phone, session.User.Phone);
        Assert.False(session.User.PhoneVerified);

        // Real tokens, from Keycloak, usable immediately. Registration that
        // hands back a session the API then refuses would be worse than one
        // that hands back nothing.
        Assert.NotEmpty(session.Tokens.AccessToken);
        Assert.NotEmpty(session.Tokens.RefreshToken);
        Assert.True(session.Tokens.ExpiresIn > 0);
        Assert.Equal("Bearer", session.Tokens.TokenType);
    }

    [SkippableFact]
    public async Task The_same_address_cannot_register_twice()
    {
        var (client, _) = Ready();
        var account = NewAccount();

        await client.PostAsJsonAsync("/v1/auth/register", account, Json);
        var second = await client.PostAsJsonAsync("/v1/auth/register", account, Json);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(IdentityErrors.EmailTaken, await CodeOf(second));
    }

    [SkippableFact]
    public async Task A_password_the_realm_policy_rejects_comes_back_as_weak_password()
    {
        var (client, _) = Ready();
        var account = NewAccount() with { Password = "short" };

        var response = await client.PostAsJsonAsync("/v1/auth/register", account, Json);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal(IdentityErrors.WeakPassword, await CodeOf(response));
    }

    [SkippableFact]
    public async Task A_phone_that_is_not_E164_is_refused_before_the_account_is_created()
    {
        var (client, _) = Ready();
        var account = NewAccount() with { Phone = "55123456" };

        var response = await client.PostAsJsonAsync("/v1/auth/register", account, Json);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal(IdentityErrors.InvalidPhone, await CodeOf(response));

        // And nothing was created, so the address is still free.
        var retry = await client.PostAsJsonAsync(
            "/v1/auth/register", account with { Phone = "+5355123456" }, Json);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    // ------------------------------------------------------------------ login

    [SkippableFact]
    public async Task A_registered_user_can_sign_in()
    {
        var (client, _) = Ready();
        var account = NewAccount();
        await client.PostAsJsonAsync("/v1/auth/register", account, Json);

        var response = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(account.Email, account.Password), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await Read<AuthSessionResponse>(response);
        Assert.Equal(account.Email, session.User.Email);
    }

    [SkippableFact]
    public async Task A_wrong_password_and_an_unknown_address_are_indistinguishable()
    {
        var (client, _) = Ready();
        var account = NewAccount();
        await client.PostAsJsonAsync("/v1/auth/register", account, Json);

        var wrongPassword = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(account.Email, "Wrong-Password-1"), Json);
        var unknownUser = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest($"nobody-{Guid.NewGuid():N}@islapay.cu", Password), Json);

        // Same status, same code. Any difference between these two is a way to
        // ask the server which e-mail addresses have accounts.
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(IdentityErrors.InvalidCredentials, await CodeOf(wrongPassword));
        Assert.Equal(IdentityErrors.InvalidCredentials, await CodeOf(unknownUser));
    }

    [SkippableFact]
    public async Task The_e_mail_is_not_case_sensitive()
    {
        var (client, _) = Ready();
        var account = NewAccount();
        await client.PostAsJsonAsync("/v1/auth/register", account, Json);

        var response = await client.PostAsJsonAsync(
            "/v1/auth/login",
            new LoginRequest(account.Email.ToUpperInvariant(), account.Password),
            Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------ refresh

    [SkippableFact]
    public async Task A_refresh_token_buys_a_new_pair()
    {
        var (client, _) = Ready();
        var session = await RegisterAsync(client);

        var response = await client.PostAsJsonAsync(
            "/v1/auth/token/refresh", new RefreshRequest(session.Tokens.RefreshToken), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pair = await Read<TokenPair>(response);

        Assert.NotEmpty(pair.AccessToken);
        Assert.True(pair.ExpiresIn > 0);
    }

    [SkippableFact]
    public async Task A_refresh_token_that_is_not_one_is_token_invalid()
    {
        var (client, _) = Ready();

        var response = await client.PostAsJsonAsync(
            "/v1/auth/token/refresh", new RefreshRequest("not-a-token"), Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(PlatformErrors.TokenInvalid, await CodeOf(response));
    }

    [SkippableFact]
    public async Task Logging_out_stops_the_refresh_token_working()
    {
        var (client, _) = Ready();
        var session = await RegisterAsync(client);

        Authorize(client, session);
        var loggedOut = await client.PostAsJsonAsync(
            "/v1/auth/logout", new RefreshRequest(session.Tokens.RefreshToken), Json);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);

        // The point of logging out. Without revocation the refresh token
        // outlives the user's decision to sign out by its full lifetime.
        var reuse = await client.PostAsJsonAsync(
            "/v1/auth/token/refresh", new RefreshRequest(session.Tokens.RefreshToken), Json);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    // ------------------------------------------------------------------ otp

    [SkippableFact]
    public async Task The_code_marks_the_phone_verified()
    {
        var (client, codes) = Ready();
        var account = NewAccount();
        var session = await RegisterAsync(client, account);

        Assert.False(session.User.PhoneVerified);

        Authorize(client, session);
        var code = codes.CodeFor(OtpPurpose.PhoneVerification, account.Phone);

        var response = await client.PostAsJsonAsync(
            "/v1/auth/otp/verify", new OtpVerifyRequest(account.Phone, code), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await Read<UserDto>(response);
        Assert.True(user.PhoneVerified);

        // And it stuck in Keycloak, not just in the response: a fresh sign-in
        // reads the account again.
        var signedIn = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(account.Email, account.Password), Json);
        Assert.True((await Read<AuthSessionResponse>(signedIn)).User.PhoneVerified);
    }

    [SkippableFact]
    public async Task A_wrong_code_leaves_the_phone_unverified()
    {
        var (client, codes) = Ready();
        var account = NewAccount();
        var session = await RegisterAsync(client, account);
        Authorize(client, session);

        var real = codes.CodeFor(OtpPurpose.PhoneVerification, account.Phone);
        var wrong = real == "000000" ? "111111" : "000000";

        var response = await client.PostAsJsonAsync(
            "/v1/auth/otp/verify", new OtpVerifyRequest(account.Phone, wrong), Json);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal(IdentityErrors.OtpInvalid, await CodeOf(response));

        var signedIn = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(account.Email, account.Password), Json);
        Assert.False((await Read<AuthSessionResponse>(signedIn)).User.PhoneVerified);
    }

    [SkippableFact]
    public async Task Verifying_someone_elses_phone_is_not_possible()
    {
        var (client, codes) = Ready();

        var victim = NewAccount();
        await RegisterAsync(client, victim);
        var victimCode = codes.CodeFor(OtpPurpose.PhoneVerification, victim.Phone);

        var attacker = NewAccount();
        var attackerSession = await RegisterAsync(client, attacker);
        Authorize(client, attackerSession);

        // Attacker holds a valid code and a valid token — for different
        // accounts. The account comes from the token, so the body cannot
        // redirect the verification onto the victim.
        var response = await client.PostAsJsonAsync(
            "/v1/auth/otp/verify", new OtpVerifyRequest(victim.Phone, victimCode), Json);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal(IdentityErrors.OtpExpired, await CodeOf(response));
    }

    [SkippableFact]
    public async Task Verifying_without_a_token_is_refused()
    {
        var (client, _) = Ready();

        var response = await client.PostAsJsonAsync(
            "/v1/auth/otp/verify", new OtpVerifyRequest("+5355123456", "123456"), Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(PlatformErrors.TokenInvalid, await CodeOf(response));
    }

    [SkippableFact]
    public async Task A_resend_produces_a_new_code_that_works()
    {
        var (client, codes) = Ready();
        var account = NewAccount();
        var session = await RegisterAsync(client, account);
        Authorize(client, session);

        var first = codes.CodeFor(OtpPurpose.PhoneVerification, account.Phone);

        var resend = await client.PostAsJsonAsync("/v1/auth/otp/resend", new { }, Json);
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);

        var second = codes.CodeFor(OtpPurpose.PhoneVerification, account.Phone);
        Assert.NotEqual(first, second);

        var verified = await client.PostAsJsonAsync(
            "/v1/auth/otp/verify", new OtpVerifyRequest(account.Phone, second), Json);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
    }

    // ------------------------------------------------------------------ tokens

    [SkippableFact]
    public async Task A_token_minted_for_another_client_in_the_same_realm_is_refused()
    {
        var (client, _) = Ready();
        var account = NewAccount();
        await RegisterAsync(client, account);

        // Signed by this realm, unexpired, for this very user — and not for
        // this API. Accepting it would mean any client in the realm is a way in.
        var stranger = await _host.Keycloak.StrangerTokenAsync(account.Email, account.Password);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stranger);
        var response = await client.PostAsJsonAsync("/v1/auth/otp/resend", new { }, Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(PlatformErrors.TokenInvalid, await CodeOf(response));
    }

    [SkippableFact]
    public async Task A_token_with_a_tampered_payload_is_refused()
    {
        var (client, _) = Ready();
        var session = await RegisterAsync(client);

        var parts = session.Tokens.AccessToken.Split('.');
        // Same header and signature, different payload. The signature no
        // longer matches, which is the entire security of a JWT.
        var forged = string.Join('.', parts[0], parts[1][..^4] + "AAAA", parts[2]);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);
        var response = await client.PostAsJsonAsync("/v1/auth/otp/resend", new { }, Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(PlatformErrors.TokenInvalid, await CodeOf(response));
    }

    // ------------------------------------------------------------------ recovery

    [SkippableFact]
    public async Task A_forgotten_password_can_be_reset_with_a_code()
    {
        var (client, codes) = Ready();
        var account = NewAccount();
        await RegisterAsync(client, account);

        var asked = await client.PostAsJsonAsync(
            "/v1/auth/password/reset",
            new PasswordResetConfirmRequest(account.Email, string.Empty, string.Empty),
            Json);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);

        var code = codes.CodeFor(OtpPurpose.PasswordReset, account.Email);
        const string Fresh = "Brand-New-Pass-4";

        var confirmed = await client.PostAsJsonAsync(
            "/v1/auth/password/reset",
            new PasswordResetConfirmRequest(account.Email, code, Fresh),
            Json);
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);

        var withNew = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(account.Email, Fresh), Json);
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);

        // The old one has to stop working, or a reset is an addition rather
        // than a replacement.
        var withOld = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(account.Email, account.Password), Json);
        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
    }

    [SkippableFact]
    public async Task Asking_to_reset_an_address_with_no_account_looks_identical()
    {
        var (client, codes) = Ready();
        var account = NewAccount();
        await RegisterAsync(client, account);

        var unknown = $"nobody-{Guid.NewGuid():N}@islapay.cu";

        var known = await client.PostAsJsonAsync(
            "/v1/auth/password/reset",
            new PasswordResetConfirmRequest(account.Email, string.Empty, string.Empty), Json);
        var stranger = await client.PostAsJsonAsync(
            "/v1/auth/password/reset",
            new PasswordResetConfirmRequest(unknown, string.Empty, string.Empty), Json);

        Assert.Equal(known.StatusCode, stranger.StatusCode);
        Assert.Equal(
            await known.Content.ReadAsStringAsync(),
            await stranger.Content.ReadAsStringAsync());

        // Nothing was sent to the address with no account, though.
        Assert.False(codes.AnythingSentTo(OtpPurpose.PasswordReset, unknown));
    }

    [SkippableFact]
    public async Task A_reset_code_for_one_address_does_not_reset_another()
    {
        var (client, codes) = Ready();

        var victim = NewAccount();
        await RegisterAsync(client, victim);

        var attacker = NewAccount();
        await RegisterAsync(client, attacker);

        await client.PostAsJsonAsync(
            "/v1/auth/password/reset",
            new PasswordResetConfirmRequest(attacker.Email, string.Empty, string.Empty), Json);
        var attackerCode = codes.CodeFor(OtpPurpose.PasswordReset, attacker.Email);

        var response = await client.PostAsJsonAsync(
            "/v1/auth/password/reset",
            new PasswordResetConfirmRequest(victim.Email, attackerCode, "Taken-Over-9"), Json);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);

        var stillTheirs = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(victim.Email, victim.Password), Json);
        Assert.Equal(HttpStatusCode.OK, stillTheirs.StatusCode);
    }

    // ------------------------------------------------------------------ health

    [SkippableFact]
    public async Task Readiness_reports_the_identity_provider()
    {
        var (client, _) = Ready();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    private (HttpClient Client, RecordingOtpSender Codes) Ready()
    {
        Skip.IfNot(_host.Available, "No Keycloak or no Postgres reachable.");
        return (_factory!.CreateClient(), _factory.Codes);
    }

    private static RegisterRequest NewAccount()
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        return new RegisterRequest(
            Name: "Ana Pérez",
            Email: $"ana-{id}@islapay.cu",
            // Unique per account: the OTP store is keyed by destination, so
            // two tests sharing a number would share a challenge.
            Phone: "+53" + Random.Shared.NextInt64(500_000_000, 599_999_999).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            Password: Password);
    }

    private static async Task<AuthSessionResponse> RegisterAsync(
        HttpClient client, RegisterRequest? account = null)
    {
        var response = await client.PostAsJsonAsync("/v1/auth/register", account ?? NewAccount(), Json);
        response.EnsureSuccessStatusCode();
        return await Read<AuthSessionResponse>(response);
    }

    private static void Authorize(HttpClient client, AuthSessionResponse session) =>
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);

    /// <summary>
    /// Deserialises a successful response, or fails with what it actually said.
    /// </summary>
    /// <remarks>
    /// Checks the status first on purpose. A problem document deserialised as
    /// a success type produces an object full of nulls and then a
    /// <see cref="NullReferenceException"/> several lines later, which says
    /// nothing about what went wrong.
    /// </remarks>
    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected a {typeof(T).Name} but got {(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, Json)
            ?? throw new InvalidOperationException("The response body was null.");
    }

    private static async Task<string> CodeOf(HttpResponseMessage response)
    {
        Assert.Equal(ProblemResults.ContentType, response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(Json);
        Assert.NotNull(problem);

        // Every failure carries one, so a user's screenshot can be found in
        // the logs.
        Assert.False(string.IsNullOrEmpty(problem!.CorrelationId));
        return problem.Code;
    }
}
