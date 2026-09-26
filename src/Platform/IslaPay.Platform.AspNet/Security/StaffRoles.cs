using System.Security.Claims;

namespace IslaPay.Platform.AspNet.Security;

/// <summary>
/// The jobs, what each one may do, and which may not be held together.
/// </summary>
/// <remarks>
/// <para>
/// The whole security model in one table, on purpose. Reading it answers the
/// questions an auditor asks — who can move money, who can approve that, who
/// can grant themselves either — without opening a single route.
/// </para>
/// <para>
/// A role is granted in the identity provider and arrives in the token; this
/// type never sees the provider. Whatever reads the token puts roles where
/// <see cref="ClaimsPrincipal.IsInRole"/> finds them, and the one claim the
/// platform defines for itself, <see cref="StaffClaims.MultiFactor"/>, says
/// whether the person proved more than a password.
/// </para>
/// </remarks>
public static class StaffRoles
{
    public const string Support = "support";
    public const string P2POperator = "p2p-operator";
    public const string P2PManager = "p2p-manager";
    public const string TreasuryOperator = "treasury-operator";
    public const string TreasuryApprover = "treasury-approver";
    public const string Compliance = "compliance";
    public const string CatalogAdmin = "catalog-admin";
    public const string SecurityAdmin = "security-admin";
    public const string Auditor = "auditor";

    /// <summary>What each role may do.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> PermissionsOf =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [Support] = Set(Permissions.SupportRead, Permissions.P2PRead),

            // Settles, and cannot change the price it settles at.
            [P2POperator] = Set(Permissions.P2PRead, Permissions.P2PSettle),

            // Sets prices and limits, and cannot settle a trade at them.
            [P2PManager] = Set(Permissions.P2PRead, Permissions.P2PManage),

            // Proposes; somebody else approves.
            [TreasuryOperator] = Set(Permissions.TreasuryRead, Permissions.TreasuryPropose),
            [TreasuryApprover] = Set(Permissions.TreasuryRead, Permissions.TreasuryApprove),

            [Compliance] = Set(
                Permissions.SupportRead, Permissions.ComplianceRead,
                Permissions.ComplianceAct, Permissions.AuditRead),

            [CatalogAdmin] = Set(Permissions.CatalogManage),

            // Grants roles and can read what was done with them; moves nothing.
            [SecurityAdmin] = Set(Permissions.SecurityManage, Permissions.AuditRead),

            // Reads everything, changes nothing.
            [Auditor] = Set(
                Permissions.SupportRead, Permissions.P2PRead, Permissions.TreasuryRead,
                Permissions.ComplianceRead, Permissions.AuditRead),
        };

    /// <summary>Roles one person may not hold together.</summary>
    /// <remarks>
    /// <para>
    /// A token carrying both halves of a pair gets neither half's
    /// permissions. Refusing both is the only reading that is safe whichever
    /// one was granted by mistake, and it is loud: the console shows the
    /// conflict instead of quietly letting the person do everything.
    /// </para>
    /// <para>
    /// The P2P operator and manager are not a pair yet. With two people on the
    /// desk, one of them has to be able to change a price at night; the pair
    /// is added here the day the team is big enough to split the jobs.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<(string First, string Second)> Conflicts =
    [
        (TreasuryOperator, TreasuryApprover),
        (SecurityAdmin, P2POperator),
        (SecurityAdmin, TreasuryOperator),
        (SecurityAdmin, TreasuryApprover),
    ];

    public static IReadOnlyList<string> All { get; } = [.. PermissionsOf.Keys];

    /// <summary>What this caller may do, and why not more.</summary>
    public static StaffAccess AccessOf(ClaimsPrincipal user, bool requireMultiFactor)
    {
        ArgumentNullException.ThrowIfNull(user);

        var held = All.Where(user.IsInRole).ToList();
        var conflicts = Conflicts
            .Where(pair => held.Contains(pair.First) && held.Contains(pair.Second))
            .Select(pair => $"{pair.First}+{pair.Second}")
            .ToList();
        var blocked = Conflicts
            .Where(pair => held.Contains(pair.First) && held.Contains(pair.Second))
            .SelectMany(pair => new[] { pair.First, pair.Second })
            .ToHashSet(StringComparer.Ordinal);

        var multiFactor = user.HasClaim(StaffClaims.MultiFactor, "true");
        var missingMultiFactor = requireMultiFactor && held.Count > 0 && !multiFactor;

        var permissions = missingMultiFactor
            ? []
            : held
                .Where(role => !blocked.Contains(role))
                .SelectMany(role => PermissionsOf[role])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

        return new StaffAccess(held, permissions, conflicts, missingMultiFactor);
    }

    private static HashSet<string> Set(params string[] permissions) =>
        new HashSet<string>(permissions, StringComparer.Ordinal);
}

/// <summary>Claims the platform defines, set by whatever reads the token.</summary>
public static class StaffClaims
{
    /// <summary>
    /// <c>"true"</c> when the person signed in with a second factor.
    /// </summary>
    public const string MultiFactor = "islapay:mfa";
}

/// <summary>
/// A caller's standing, as <c>GET /v1/me/permissions</c> reports it.
/// </summary>
/// <param name="Roles">Staff roles the token carries.</param>
/// <param name="Permissions">What those roles allow, after conflicts and the second factor.</param>
/// <param name="Conflicts">Pairs of roles that cancelled each other, as <c>a+b</c>.</param>
/// <param name="MultiFactorRequired">
/// True when the roles would allow something but the sign-in had no second
/// factor, so they allow nothing until the person signs in again with one.
/// </param>
public sealed record StaffAccess(
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Conflicts,
    bool MultiFactorRequired);
