using System.Security.Claims;
using IslaPay.Platform.AspNet.Security;

namespace IslaPay.Identity.Tests;

/// <summary>
/// The one place a Keycloak token's roles are read.
/// </summary>
public class RealmRoleClaimsTests
{
    private readonly RealmRoleClaims _claims = new();

    [Fact]
    public async Task Realm_roles_become_roles_the_platform_can_see()
    {
        var person = await _claims.TransformAsync(Token(
            new Claim("realm_access", """{"roles":["p2p-operator","offline_access"]}""")));

        Assert.True(person.IsInRole(StaffRoles.P2POperator));
        Assert.True(person.IsInRole("offline_access"));
        Assert.False(person.IsInRole(StaffRoles.TreasuryApprover));
    }

    [Fact]
    public async Task Reading_twice_adds_nothing_the_second_time()
    {
        var person = Token(new Claim("realm_access", """{"roles":["auditor"]}"""));
        await _claims.TransformAsync(person);
        await _claims.TransformAsync(person);

        Assert.Single(person.FindAll(ClaimTypes.Role), c => c.Value == StaffRoles.Auditor);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"roles":"p2p-operator"}""")]
    [InlineData("""["p2p-operator"]""")]
    [InlineData("""{"roles":[1,2]}""")]
    public async Task A_claim_that_will_not_parse_grants_nothing(string realmAccess)
    {
        var person = await _claims.TransformAsync(Token(new Claim("realm_access", realmAccess)));

        Assert.Empty(StaffRoles.AccessOf(person, requireMultiFactor: false).Roles);
    }

    [Theory]
    [InlineData("otp", true)]
    [InlineData("""["pwd","otp"]""", true)]
    [InlineData("pwd", false)]
    [InlineData("""["pwd"]""", false)]
    public async Task A_second_factor_is_read_from_amr(string amr, bool expected)
    {
        var person = await _claims.TransformAsync(Token(new Claim("amr", amr)));

        Assert.Equal(expected, person.HasClaim(StaffClaims.MultiFactor, "true"));
    }

    [Fact]
    public async Task A_second_factor_is_read_under_the_name_the_bearer_handler_gives_amr()
    {
        var person = await _claims.TransformAsync(Token(
            new Claim("http://schemas.microsoft.com/claims/authnmethodsreferences", "otp")));

        Assert.True(person.HasClaim(StaffClaims.MultiFactor, "true"));
    }

    [Fact]
    public async Task An_anonymous_caller_is_left_alone()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("realm_access", """{"roles":["auditor"]}""")]));

        var person = await _claims.TransformAsync(anonymous);

        Assert.False(person.IsInRole(StaffRoles.Auditor));
    }

    private static ClaimsPrincipal Token(params Claim[] claims) =>
        new(new ClaimsIdentity([new Claim("sub", "someone"), .. claims], "Bearer"));
}
