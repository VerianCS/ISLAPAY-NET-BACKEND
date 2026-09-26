using System.Security.Claims;
using IslaPay.Platform.AspNet.Security;

namespace IslaPay.Platform.AspNet.Tests;

/// <summary>
/// The separation of duties, as properties of the table rather than of a route.
/// </summary>
/// <remarks>
/// Each test states a rule an auditor would ask about. Adding a role or a
/// permission that breaks one fails here, before a route ever sees it.
/// </remarks>
public class StaffRolesTests
{
    [Fact]
    public void Every_role_grants_only_permissions_that_exist()
    {
        foreach (var (role, permissions) in StaffRoles.PermissionsOf)
        {
            Assert.All(permissions, p => Assert.Contains(p, Permissions.All));
            Assert.NotEmpty(permissions);
            Assert.False(string.IsNullOrWhiteSpace(role));
        }
    }

    [Fact]
    public void Every_permission_is_granted_by_some_role()
    {
        // A permission nobody can hold is a route nobody can call.
        var granted = StaffRoles.PermissionsOf.Values.SelectMany(p => p).ToHashSet();
        Assert.All(Permissions.All, p => Assert.Contains(p, granted));
    }

    [Fact]
    public void No_single_role_both_proposes_and_approves()
    {
        Assert.DoesNotContain(StaffRoles.PermissionsOf, r =>
            r.Value.Contains(Permissions.TreasuryPropose) && r.Value.Contains(Permissions.TreasuryApprove));
    }

    [Fact]
    public void Nobody_who_grants_roles_can_move_money()
    {
        Assert.DoesNotContain(StaffRoles.PermissionsOf, r =>
            r.Value.Contains(Permissions.SecurityManage) && r.Value.Overlaps(Permissions.MoveMoney));
    }

    [Fact]
    public void Whoever_grants_roles_may_not_also_hold_one_that_moves_money()
    {
        // Otherwise the rule above is one self-grant away from meaningless.
        var granters = StaffRoles.PermissionsOf
            .Where(r => r.Value.Contains(Permissions.SecurityManage)).Select(r => r.Key);
        var movers = StaffRoles.PermissionsOf
            .Where(r => r.Value.Overlaps(Permissions.MoveMoney)).Select(r => r.Key);

        foreach (var granter in granters)
        {
            foreach (var mover in movers)
            {
                Assert.Contains(StaffRoles.Conflicts, pair =>
                    (pair.First == granter && pair.Second == mover)
                    || (pair.First == mover && pair.Second == granter));
            }
        }
    }

    [Fact]
    public void The_auditor_reads_and_changes_nothing()
    {
        var auditor = StaffRoles.PermissionsOf[StaffRoles.Auditor];
        Assert.All(auditor, p => Assert.EndsWith(".read", p, StringComparison.Ordinal));
    }

    [Fact]
    public void A_customer_has_no_permissions()
    {
        var access = StaffRoles.AccessOf(Person(), requireMultiFactor: true);

        Assert.Empty(access.Roles);
        Assert.Empty(access.Permissions);
        Assert.False(access.MultiFactorRequired);
    }

    [Fact]
    public void A_role_grants_its_permissions()
    {
        var access = StaffRoles.AccessOf(Person(StaffRoles.P2POperator), requireMultiFactor: false);

        Assert.Equal([Permissions.P2PRead, Permissions.P2PSettle], access.Permissions);
        Assert.DoesNotContain(Permissions.P2PManage, access.Permissions);
    }

    [Fact]
    public void Roles_that_conflict_cancel_each_other_and_say_so()
    {
        var access = StaffRoles.AccessOf(
            Person(StaffRoles.TreasuryOperator, StaffRoles.TreasuryApprover, StaffRoles.CatalogAdmin),
            requireMultiFactor: false);

        Assert.Equal(["treasury-operator+treasury-approver"], access.Conflicts);
        Assert.Equal([Permissions.CatalogManage], access.Permissions);
    }

    [Fact]
    public void Without_a_second_factor_a_role_grants_nothing_where_one_is_required()
    {
        var password = StaffRoles.AccessOf(Person(StaffRoles.P2POperator), requireMultiFactor: true);
        Assert.Empty(password.Permissions);
        Assert.True(password.MultiFactorRequired);

        var withCode = StaffRoles.AccessOf(
            Person([StaffRoles.P2POperator], multiFactor: true), requireMultiFactor: true);
        Assert.Contains(Permissions.P2PSettle, withCode.Permissions);
        Assert.False(withCode.MultiFactorRequired);
    }

    [Fact]
    public void An_unknown_role_grants_nothing()
    {
        var access = StaffRoles.AccessOf(Person("offline_access", "admin"), requireMultiFactor: false);
        Assert.Empty(access.Roles);
        Assert.Empty(access.Permissions);
    }

    private static ClaimsPrincipal Person(params string[] roles) => Person(roles, multiFactor: false);

    private static ClaimsPrincipal Person(string[] roles, bool multiFactor)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r)).ToList();
        claims.Add(new Claim("sub", "someone"));
        if (multiFactor) claims.Add(new Claim(StaffClaims.MultiFactor, "true"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
