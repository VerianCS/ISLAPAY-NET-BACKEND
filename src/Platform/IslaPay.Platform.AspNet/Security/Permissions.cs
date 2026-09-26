namespace IslaPay.Platform.AspNet.Security;

/// <summary>
/// What a member of staff may do, one verb at a time.
/// </summary>
/// <remarks>
/// <para>
/// Routes ask for a permission, never for a role. A role is a job — "the
/// person who pays out sales" — and jobs change shape as the team grows; a
/// permission is a single thing the API can refuse. Asking for the second is
/// what lets a job be split in two without touching a route.
/// </para>
/// <para>
/// Strings in the platform, although each belongs to a module, because the
/// map from roles to permissions has to be one table somebody can read in one
/// sitting. Separation of duties is a property of the whole map — "nobody can
/// both propose and approve" — and it cannot be checked if every module keeps
/// its own half.
/// </para>
/// </remarks>
public static class Permissions
{
    /// <summary>Look a customer up and read what happened to them.</summary>
    public const string SupportRead = "support.read";

    /// <summary>See the P2P queue and search trades.</summary>
    public const string P2PRead = "p2p.read";

    /// <summary>Mark a trade paid, received or failed. Moves money.</summary>
    public const string P2PSettle = "p2p.settle";

    /// <summary>Prices, methods, limits, instructions, on and off.</summary>
    public const string P2PManage = "p2p.manage";

    /// <summary>The platform's balances, reconciliation and entries.</summary>
    public const string TreasuryRead = "treasury.read";

    /// <summary>Ask for money to enter the books, or E-ISLA to be issued or retired.</summary>
    public const string TreasuryPropose = "treasury.propose";

    /// <summary>Approve somebody else's proposal. Never one's own.</summary>
    public const string TreasuryApprove = "treasury.approve";

    /// <summary>Switch currencies, chains and assets on and off.</summary>
    public const string CatalogManage = "catalog.manage";

    /// <summary>Read verification levels, freezes and the reasons for them.</summary>
    public const string ComplianceRead = "compliance.read";

    /// <summary>Freeze or unfreeze an account, and change its verification level.</summary>
    public const string ComplianceAct = "compliance.act";

    /// <summary>Read the audit log.</summary>
    public const string AuditRead = "audit.read";

    /// <summary>Grant and withdraw staff roles.</summary>
    public const string SecurityManage = "security.manage";

    public static readonly IReadOnlyList<string> All =
    [
        SupportRead,
        P2PRead, P2PSettle, P2PManage,
        TreasuryRead, TreasuryPropose, TreasuryApprove,
        CatalogManage,
        ComplianceRead, ComplianceAct,
        AuditRead,
        SecurityManage,
    ];

    /// <summary>The permissions whose holder can move or create money.</summary>
    /// <remarks>
    /// What <see cref="StaffRoles"/> keeps away from whoever grants roles: a
    /// person who can give themselves a role that moves money can move money.
    /// </remarks>
    public static readonly IReadOnlySet<string> MoveMoney = new HashSet<string>(StringComparer.Ordinal)
    {
        P2PSettle, TreasuryPropose, TreasuryApprove,
    };

    /// <summary>The authorisation policy a permission is enforced by.</summary>
    public static string PolicyFor(string permission) => $"permission:{permission}";
}
