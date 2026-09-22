namespace IslaPay.Custody;

/// <summary>Knobs, bound from the <c>Custody</c> configuration section.</summary>
public sealed class CustodyOptions
{
    /// <summary>
    /// How long a deposit may sit in <c>crediting</c> before the sweeper
    /// investigates it.
    /// </summary>
    /// <remarks>
    /// Long enough that a credit taking its time is not mistaken for one that
    /// died. The sweeper is safe either way — it asks the ledger rather than
    /// assuming — but waking on a request that is still in flight wastes a
    /// round trip and muddies the logs.
    /// </remarks>
    public TimeSpan InFlightGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How often the sweeper looks.</summary>
    public TimeSpan SweepEvery { get; init; } = TimeSpan.FromMinutes(1);
}
