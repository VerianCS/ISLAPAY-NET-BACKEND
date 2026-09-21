using IslaPay.Platform;

namespace IslaPay.P2P.Contracts;

/// <summary>What P2P publishes, and where.</summary>
public static class P2PEvents
{
    /// <summary>The bounded context, and therefore the exchange <c>x.p2p</c>.</summary>
    public const string Context = "p2p";

    /// <summary>Every trade event, for a consumer that wants the lot.</summary>
    public const string AllTradeEvents = "trade.*.v1";

    /// <summary>
    /// A sell's wallet money has left the user and the local leg is owed.
    /// </summary>
    /// <remarks>
    /// This is the one an operator's console should wake up on: it means there
    /// is money to send and a person waiting for it.
    /// </remarks>
    public const string TradeCommitted = "trade.committed.v1";

    /// <summary>The local leg settled: paid out, or received and credited.</summary>
    public const string TradeCompleted = "trade.completed.v1";

    /// <summary>A sell could not be paid and the user has their money back.</summary>
    public const string TradeRefunded = "trade.refunded.v1";
}

/// <summary>A sell that has taken the user's money and owes them local currency.</summary>
/// <param name="Local">What IslaPay now owes, in the rail's currency.</param>
public sealed record TradeCommitted(
    Guid TradeId,
    string UserId,
    string MethodId,
    Money Amount,
    Money Local,
    string Reference,
    DateTimeOffset OccurredAt);

/// <summary>The trade did what it said it would.</summary>
/// <param name="Side">
/// Which way, because the two mean opposite things to a consumer: a completed
/// sell is money that left, a completed buy is money that arrived.
/// </param>
/// <param name="OperatorReference">The bank's confirmation number.</param>
public sealed record TradeCompleted(
    Guid TradeId,
    string UserId,
    P2PSide Side,
    string MethodId,
    Money Amount,
    Money Local,
    string OperatorReference,
    DateTimeOffset OccurredAt);

/// <summary>A sell reversed. The user's wallet is whole again, fee included.</summary>
public sealed record TradeRefunded(
    Guid TradeId,
    string UserId,
    string MethodId,
    Money Amount,
    string Reason,
    DateTimeOffset OccurredAt);
