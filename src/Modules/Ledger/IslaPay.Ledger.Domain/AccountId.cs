using IslaPay.Platform;

namespace IslaPay.Ledger.Domain;

/// <summary>What an account belongs to. See the chart of accounts, §8.4.</summary>
public enum OwnerType
{
    /// <summary>A customer. Liability: what we owe them.</summary>
    User,

    /// <summary>A merchant's collected funds. Liability.</summary>
    Merchant,

    /// <summary>One of the platform's own accounts.</summary>
    Platform,

    /// <summary>Mirror of something held outside: a bank, a custodian.</summary>
    External,
}

/// <summary>
/// Accounting classification. Decides the sign convention a reader should
/// expect, and keeps revenue separable from float in reporting.
/// </summary>
public enum AccountType
{
    Asset,
    Liability,
    Revenue,
    Expense,
}

/// <summary>
/// An account in the ledger, named as in §8.4: <c>user:42:EISLA</c>,
/// <c>platform:fees:USD</c>, <c>external:chain:USDT</c>.
/// </summary>
/// <remarks>
/// The name is structured rather than free text because it is what appears in
/// every reconciliation report and every support conversation, and a typo in a
/// free-text account name is a posting that silently goes somewhere else.
/// </remarks>
public readonly record struct AccountId
{
    private AccountId(OwnerType ownerType, string owner, Currency currency)
    {
        OwnerType = ownerType;
        Owner = owner;
        Currency = currency;
    }

    public OwnerType OwnerType { get; }

    /// <summary>The user id, merchant id, or the platform account's role.</summary>
    public string Owner { get; }

    public Currency Currency { get; }

    public static AccountId User(string userId, Currency currency) =>
        new(OwnerType.User, Require(userId), currency);

    public static AccountId Merchant(string merchantId, Currency currency) =>
        new(OwnerType.Merchant, Require(merchantId), currency);

    /// <summary>Fee revenue: <c>platform:fees:{ccy}</c>.</summary>
    public static AccountId Fees(Currency currency) =>
        new(OwnerType.Platform, "fees", currency);

    /// <summary>The exchange fund the app shows: <c>platform:settlement_fund:{ccy}</c>.</summary>
    public static AccountId SettlementFund(Currency currency) =>
        new(OwnerType.Platform, "settlement_fund", currency);

    /// <summary>Funds held for P2P and partner bookings.</summary>
    public static AccountId Escrow(Currency currency) =>
        new(OwnerType.Platform, "escrow", currency);

    /// <summary>
    /// Cash at bank or custodian: <c>platform:float:{ccy}</c>.
    /// </summary>
    /// <remarks>
    /// Named <c>CashFloat</c> rather than <c>Float</c> because the latter
    /// reads as the numeric type. The account's own name stays <c>float</c>,
    /// as the chart of accounts in §8.4 has it.
    /// </remarks>
    public static AccountId CashFloat(Currency currency) =>
        new(OwnerType.Platform, "float", currency);

    public static AccountId External(string mirror, Currency currency) =>
        new(OwnerType.External, Require(mirror), currency);

    /// <summary>
    /// The accounting type, derived rather than stored: a user account is a
    /// liability by construction, and letting a caller declare otherwise would
    /// only allow it to be declared wrongly.
    /// </summary>
    public AccountType Type => OwnerType switch
    {
        OwnerType.User => AccountType.Liability,
        OwnerType.Merchant => AccountType.Liability,
        OwnerType.External => AccountType.Asset,
        OwnerType.Platform => Owner switch
        {
            "fees" => AccountType.Revenue,
            "escrow" => AccountType.Liability,
            _ => AccountType.Asset,
        },
        _ => throw new InvalidOperationException($"Unclassified owner type {OwnerType}."),
    };

    /// <summary>
    /// Whether this account is allowed to hold a negative balance.
    /// </summary>
    /// <remarks>
    /// Customer and merchant money may not go negative — that is credit, and
    /// IslaPay does not extend it (§8.3.3). Platform and external accounts
    /// must be allowed to: the float is negative while money is in flight, and
    /// a mirror account is negative by definition when it represents what we
    /// hold elsewhere.
    /// </remarks>
    public bool MayGoNegative => OwnerType is not (OwnerType.User or OwnerType.Merchant);

    private static string Require(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("An account owner cannot be blank.", nameof(value))
            : value;

    public override string ToString() =>
        $"{OwnerType.ToString().ToLowerInvariant()}:{Owner}:{Currency.Code}";
}
