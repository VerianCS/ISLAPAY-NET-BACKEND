using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IslaPay.Identity.Contracts;
using IslaPay.P2P.Contracts;
using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet.Security;
using IslaPay.Platform.Data;
using IslaPay.Platform.Serialization;
using IslaPay.TestSupport;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// Who may do what, through the running host and a real Keycloak token.
/// </summary>
/// <remarks>
/// The table itself is tested in the platform. These prove the parts only a
/// real request can: that Keycloak's roles reach the table, that every admin
/// route consults it, and that a split role really is split.
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class SecurityTests
{
    private const string Password = "Correct-Horse-9";
    private static readonly JsonSerializerOptions Json = IslaPayJson.Create(TestCurrencies.Scales);

    private readonly IslaPayHostFixture _fixture;

    public SecurityTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Every_admin_route_asks_for_a_permission()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        using var _ = host.CreateClient();

        // A route under /v1/admin with only "signed in" on it is a route every
        // customer can call. Easy to write, invisible in review, and the one
        // mistake this model exists to make impossible.
        var admin = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/v1/admin", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(admin);
        var bare = admin
            .Where(e => e.Metadata.GetMetadata<PermissionMetadata>() is null)
            .Select(e => $"{e.DisplayName}")
            .ToList();
        Assert.True(bare.Count == 0, "Admin routes without a permission: " + string.Join(", ", bare));
    }

    [SkippableFact]
    public async Task A_customer_is_told_they_hold_nothing()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var customer = await StaffAsync(host);

        var access = await AccessAsync(host, customer);

        Assert.Empty(access.Roles);
        Assert.Empty(access.Permissions);
        Assert.Empty(access.Conflicts);
    }

    [SkippableFact]
    public async Task The_desk_operator_settles_and_cannot_set_a_price()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var operador = await StaffAsync(host, StaffRoles.P2POperator);

        var access = await AccessAsync(host, operador);
        Assert.Equal([Permissions.P2PRead, Permissions.P2PSettle], access.Permissions);

        using var client = Client(host, operador);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(
            new Uri("/v1/admin/p2p/queue", UriKind.Relative))).StatusCode);

        var price = await client.PutAsJsonAsync(
            "/v1/admin/p2p/rates",
            new P2PRateUpdate("cup", P2PSide.Buy, "EISLA", "130"), Json);
        Assert.Equal(HttpStatusCode.Forbidden, price.StatusCode);
    }

    [SkippableFact]
    public async Task The_desk_manager_sets_prices_and_cannot_settle()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var manager = await StaffAsync(host, StaffRoles.P2PManager);

        using var client = Client(host, manager);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(
            new Uri("/v1/admin/p2p/methods", UriKind.Relative))).StatusCode);

        // Refused before the trade is looked up: the id need not exist.
        using var settle = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"/v1/admin/p2p/trades/{Guid.NewGuid()}/paid", UriKind.Relative))
        {
            Content = JsonContent.Create(new P2PSettleRequest("TM-1"), options: Json),
        };
        settle.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(settle)).StatusCode);
    }

    [SkippableFact]
    public async Task Proposing_and_approving_together_grants_neither()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var both = await StaffAsync(host, StaffRoles.TreasuryOperator, StaffRoles.TreasuryApprover);

        var access = await AccessAsync(host, both);
        Assert.Equal(["treasury-operator+treasury-approver"], access.Conflicts);
        Assert.Empty(access.Permissions);

        using var client = Client(host, both);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(
            new Uri("/v1/admin/treasury/balances", UriKind.Relative))).StatusCode);
    }

    [SkippableFact]
    public async Task The_auditor_reads_the_treasury_and_cannot_credit_it()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var auditor = await StaffAsync(host, StaffRoles.Auditor);

        using var client = Client(host, auditor);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(
            new Uri("/v1/admin/treasury/balances", UriKind.Relative))).StatusCode);

        using var credit = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/v1/admin/treasury/credits", UriKind.Relative))
        {
            Content = JsonContent.Create(new { }, options: Json),
        };
        credit.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(credit)).StatusCode);
    }

    [SkippableFact]
    public async Task Where_a_second_factor_is_required_a_password_alone_grants_nothing()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        host.Settings["Security:RequireMultiFactor"] = "true";

        var email = $"staff-{Guid.NewGuid():N}"[..16] + "@islapay.cu";
        var (_, secret) = await _fixture.Keycloak.StaffWithAuthenticatorAsync(
            email, Password, StaffRoles.P2POperator);

        using var client = host.CreateClient();

        // An account with an authenticator is refused without its code, and
        // refused the way a wrong password is: nothing says which was missing.
        var without = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(email, Password), Json);
        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);

        var with = await client.PostAsJsonAsync(
            "/v1/auth/login",
            new LoginRequest(email, Password, KeycloakFixture.CurrentCode(secret)), Json);
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
        var session = (await with.Content.ReadFromJsonAsync<AuthSessionResponse>(Json))!;

        var staff = new Account("", email, session.Tokens.AccessToken);
        var access = await AccessAsync(host, staff);
        Assert.False(access.MultiFactorRequired);
        Assert.Contains(Permissions.P2PSettle, access.Permissions);
    }

    [SkippableFact]
    public async Task Where_a_second_factor_is_required_a_role_without_one_is_reported_not_granted()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        host.Settings["Security:RequireMultiFactor"] = "true";
        var operador = await StaffAsync(host, StaffRoles.P2POperator);

        var access = await AccessAsync(host, operador);
        Assert.True(access.MultiFactorRequired);
        Assert.Empty(access.Permissions);

        using var client = Client(host, operador);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(
            new Uri("/v1/admin/p2p/queue", UriKind.Relative))).StatusCode);
    }

    [SkippableFact]
    public async Task What_staff_change_and_what_they_are_refused_is_in_the_audit_log()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var manager = await StaffAsync(host, StaffRoles.P2PManager);
        var auditor = await StaffAsync(host, StaffRoles.Auditor);

        using (var client = Client(host, manager))
        {
            // One change that runs and one the role does not allow.
            var instructions = await client.PutAsJsonAsync(
                "/v1/admin/p2p/methods/cup/instructions",
                new P2PInstructionsUpdate("Transfermóvil al 9205 1299 0000 1234"), Json);
            Assert.Equal(HttpStatusCode.NoContent, instructions.StatusCode);

            using var settle = new HttpRequestMessage(
                HttpMethod.Post, new Uri($"/v1/admin/p2p/trades/{Guid.NewGuid()}/paid", UriKind.Relative))
            {
                Content = JsonContent.Create(new P2PSettleRequest("TM-1"), options: Json),
            };
            settle.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(settle)).StatusCode);

            // Only an auditor's kind of role reads the log.
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(
                new Uri("/v1/admin/audit", UriKind.Relative))).StatusCode);
        }

        using var reader = Client(host, auditor);
        var page = (await reader.GetFromJsonAsync<CursorPage<AuditEntryDto>>(
            $"/v1/admin/audit?actor={Uri.EscapeDataString(manager.UserId)}&limit=10", Json))!;

        var changed = Assert.Single(page.Items, e => e.Kind == "route");
        Assert.Equal("PUT /v1/admin/p2p/methods/{id}/instructions", changed.Action);
        Assert.Equal(Permissions.P2PManage, changed.Permission);
        Assert.Equal("ok", changed.Outcome);
        Assert.Equal("cup", changed.Details["id"]);
        Assert.Equal(manager.Email, changed.ActorName);

        Assert.Equal(2, page.Items.Count(e => e.Kind == "denied"));
        Assert.Contains(page.Items, e => e.Kind == "denied" && e.Permission == Permissions.P2PSettle);
    }

    [SkippableFact]
    public async Task The_audit_log_is_a_chain_and_refuses_to_be_edited()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var manager = await StaffAsync(host, StaffRoles.P2PManager);
        using (var client = Client(host, manager))
        {
            await client.PutAsJsonAsync(
                "/v1/admin/p2p/methods/cup/instructions", new P2PInstructionsUpdate("Uno"), Json);
            await client.PutAsJsonAsync(
                "/v1/admin/p2p/methods/cup/instructions", new P2PInstructionsUpdate("Dos"), Json);
        }

        var database = host.Services.GetRequiredService<IDatabase>();
        await using var connection = await database.OpenAsync();

        // Each row names the one before it.
        await using (var chain = new NpgsqlCommand(
            "SELECT count(*) FROM platform.audit_log a "
            + "JOIN platform.audit_log b ON b.seq = "
            + "(SELECT max(seq) FROM platform.audit_log WHERE seq < a.seq) "
            + "WHERE a.prev_hash <> b.hash;", connection))
        {
            Assert.Equal(0L, (long)(await chain.ExecuteScalarAsync())!);
        }

        await using var edit = new NpgsqlCommand(
            "UPDATE platform.audit_log SET outcome = 'ok' "
            + "WHERE seq = (SELECT max(seq) FROM platform.audit_log);", connection);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => edit.ExecuteNonQueryAsync());
        Assert.Contains("append-only", refused.MessageText, StringComparison.Ordinal);

        await using var delete = new NpgsqlCommand("DELETE FROM platform.audit_log;", connection);
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Account(string UserId, string Email, string AccessToken);

    /// <summary>A registered account holding exactly these roles, signed in after the grant.</summary>
    private async Task<Account> StaffAsync(IslaPayHost host, params string[] roles)
    {
        using var client = host.CreateClient();

        var id = Guid.NewGuid().ToString("N")[..10];
        var email = $"staff-{id}@islapay.cu";
        var phone = "+53" + Random.Shared.NextInt64(500_000_000, 599_999_999)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        var registered = await client.PostAsJsonAsync(
            "/v1/auth/register", new RegisterRequest("Ana Pérez", email, phone, Password), Json);
        registered.EnsureSuccessStatusCode();
        var session = (await registered.Content.ReadFromJsonAsync<AuthSessionResponse>(Json))!;

        foreach (var role in roles)
            await _fixture.Keycloak.GrantRealmRoleAsync(session.User.Id, role);

        var login = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(email, Password), Json);
        login.EnsureSuccessStatusCode();
        var fresh = (await login.Content.ReadFromJsonAsync<AuthSessionResponse>(Json))!;

        return new Account(session.User.Id, email, fresh.Tokens.AccessToken);
    }

    private static async Task<StaffAccess> AccessAsync(IslaPayHost host, Account account)
    {
        using var client = Client(host, account);
        var response = await client.GetAsync(new Uri("/v1/me/permissions", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StaffAccess>(Json))!;
    }

    private static HttpClient Client(IslaPayHost host, Account account)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", account.AccessToken);
        return client;
    }
}
