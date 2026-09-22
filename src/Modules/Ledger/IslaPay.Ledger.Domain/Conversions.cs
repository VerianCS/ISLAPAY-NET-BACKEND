using IslaPay.Platform;

namespace IslaPay.Ledger.Domain;

/// <summary>Fees, in basis points. 1 bp = 0.01%, so 1% is 100.</summary>
/// <remarks>
/// Basis points rather than a fraction, so a fee is an integer multiplication
/// and never a floating-point one. Two independent figures that currently hold
/// the same value — keep them separate so they can diverge without a code
/// change at every call site.
/// <para>
/// Note for anyone reading §8.5 of the architecture: it works its example at
/// 3% on conversions, which was the figure at the time. The product rule is
/// now 1% on both, matching the client.
/// </para>
/// </remarks>
public static class Fees
{
    public const int ConversionBps = 100;
    public const int P2PBps = 100;

    /// <summary>
    /// How a fee settles a fraction of a minor unit.
    /// </summary>
    /// <remarks>
    /// Half-to-even: it does not bias the fund in either direction across many
    /// operations, which round-half-up would. Stated here once so the ledger
    /// and the client cannot pick differently and disagree by a cent.
    /// </remarks>
    public const MidpointRounding Rounding = MidpointRounding.ToEven;
}

/// <summary>
/// Builds the postings for the product's money movements, so the leg structure
/// lives in one place rather than being reinvented per service.
/// </summary>
public static class Conversions
{
    /// <summary>
    /// A currency conversion, exactly as §8.5 lays it out: the customer is
    /// debited, the fund takes the principal, fee revenue is recognised, and
    /// the fund releases the destination currency.
    /// </summary>
    /// <remarks>
    /// The fee is taken in the source currency, which is what the client shows
    /// ("Comisión (1%, en E-ISLA)"). Both currencies balance independently.
    /// <para>
    /// Whether the fund can cover the destination leg is not decided here —
    /// <see cref="Ledger.Post"/> rejects the posting if it cannot, and that is
    /// the only place the answer is authoritative. A check made anywhere
    /// earlier is a guess that was true when it was made.
    /// </para>
    /// </remarks>
    public static Posting BuildConversion(
        Guid id,
        string userId,
        Money amount,
        Currency to,
        string rate,
        DateTimeOffset at,
        string? idempotencyKey = null,
        Guid? correlationId = null)
    {
        if (!amount.IsPositive)
        {
            throw new ArgumentException("A conversion must move a positive amount.", nameof(amount));
        }

        if (amount.Currency == to)
        {
            throw new ArgumentException(
                $"Converting {to.Code} to itself is not a conversion.", nameof(to));
        }

        var from = amount.Currency;
        var fee = amount.MultiplyByBasisPoints(Fees.ConversionBps, Fees.Rounding);
        var principal = amount - fee;
        var received = principal.ConvertTo(to, decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), Fees.Rounding);

        if (!received.IsPositive)
        {
            throw new ArgumentException(
                $"{amount} at {rate} nets nothing in {to.Code}; the amount is too small to convert.",
                nameof(amount));
        }

        // The fee leg is omitted when the fee rounds to nothing.
        //
        // One per cent of twelve cents is a hundredth of a cent, and E-ISLA is
        // accounted in cents: there is no way to charge it, so nothing is
        // charged. Posting a zero leg instead is not an option — a leg that
        // moves nothing is refused by the domain, which turned a conversion of
        // a small amount into a crash rather than a conversion. Found by the
        // property test, at 0.12.
        List<Leg> legs =
        [
            new Leg(AccountId.User(userId, from), -amount),
            new Leg(AccountId.SettlementFund(from), principal),
            new Leg(AccountId.SettlementFund(to), -received),
            new Leg(AccountId.User(userId, to), received),
        ];

        if (fee.IsPositive) legs.Insert(2, new Leg(AccountId.Fees(from), fee));

        return new Posting(
            id: id,
            kind: TransactionKind.Conversion,
            legs: legs,
            postedAt: at,
            idempotencyKey: idempotencyKey,
            correlationId: correlationId,
            metadata: new Dictionary<string, string>
            {
                ["rate"] = rate,
                ["feeBps"] = Fees.ConversionBps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    /// <summary>A transfer between two customers in the same currency.</summary>
    public static Posting BuildTransfer(
        Guid id,
        string fromUserId,
        string toUserId,
        Money amount,
        DateTimeOffset at,
        string? idempotencyKey = null,
        Guid? correlationId = null)
    {
        if (!amount.IsPositive)
        {
            throw new ArgumentException("A transfer must move a positive amount.", nameof(amount));
        }

        if (fromUserId == toUserId)
        {
            throw new ArgumentException("A transfer needs two different parties.", nameof(toUserId));
        }

        return new Posting(
            id: id,
            kind: TransactionKind.Transfer,
            legs:
            [
                new Leg(AccountId.User(fromUserId, amount.Currency), -amount),
                new Leg(AccountId.User(toUserId, amount.Currency), amount),
            ],
            postedAt: at,
            idempotencyKey: idempotencyKey,
            correlationId: correlationId);
    }

    /// <summary>
    /// A P2P sale: the customer gives up the wallet currency, the fee is
    /// recognised and the rest goes to the fund, which settles the local leg
    /// outside IslaPay.
    /// </summary>
    public static Posting BuildP2PSale(
        Guid id,
        string userId,
        Money amount,
        DateTimeOffset at,
        string? idempotencyKey = null,
        Guid? correlationId = null)
    {
        if (!amount.IsPositive)
        {
            throw new ArgumentException("A P2P trade must move a positive amount.", nameof(amount));
        }

        var fee = amount.MultiplyByBasisPoints(Fees.P2PBps, Fees.Rounding);
        var principal = amount - fee;

        // Same as a conversion: a fee that rounds to nothing is not charged,
        // and must not be posted as a zero leg.
        List<Leg> legs =
        [
            new Leg(AccountId.User(userId, amount.Currency), -amount),
            new Leg(AccountId.SettlementFund(amount.Currency), principal),
        ];

        if (fee.IsPositive) legs.Add(new Leg(AccountId.Fees(amount.Currency), fee));

        return new Posting(
            id: id,
            kind: TransactionKind.Payment,
            legs: legs,
            postedAt: at,
            idempotencyKey: idempotencyKey,
            correlationId: correlationId,
            metadata: new Dictionary<string, string>
            {
                ["feeBps"] = Fees.P2PBps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    /// <summary>A deposit: money arrives from outside and the customer is credited.</summary>
    public static Posting BuildDeposit(
        Guid id,
        string userId,
        Money amount,
        string source,
        DateTimeOffset at,
        string? idempotencyKey = null)
    {
        if (!amount.IsPositive)
        {
            throw new ArgumentException("A deposit must be positive.", nameof(amount));
        }

        return new Posting(
            id: id,
            kind: TransactionKind.Settlement,
            legs:
            [
                new Leg(AccountId.External(source, amount.Currency), -amount),
                new Leg(AccountId.User(userId, amount.Currency), amount),
            ],
            postedAt: at,
            idempotencyKey: idempotencyKey);
    }
}
