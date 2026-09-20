using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using IslaPay.Wallet.Contracts;

namespace IslaPay.Wallet;

/// <summary>
/// Turns ledger entries into the screen the app shows.
/// </summary>
/// <remarks>
/// <para>
/// This is why Wallet is a module and not a pass-through. The ledger
/// classifies a movement for reconciliation — <c>settlement</c>,
/// <c>conversion</c> — and the app shows something else entirely:
/// <c>transfer_sent</c> and <c>transfer_received</c> are the same posting seen
/// from two sides, and which one a user sees depends on the sign of their own
/// leg. A ledger that knew that would be a ledger with a view baked into it.
/// </para>
/// </remarks>
public sealed class WalletService
{
    /// <summary>
    /// The currencies every account holder gets.
    /// </summary>
    /// <remarks>
    /// All of them, at zero, from the moment the account exists. A wallet that
    /// hides a currency until it has been used looks broken to someone who has
    /// just signed up and wants to know where to send money.
    /// </remarks>
    public static readonly IReadOnlyList<Currency> OpenedOnRegistration =
        [Currency.Usd, Currency.Usdc, Currency.Usdt];

    private readonly ILedger _ledger;

    public WalletService(ILedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
    }

    /// <summary>Opens the user's accounts if they are not already open.</summary>
    public Task OpenAccountsAsync(string userId, CancellationToken cancellationToken = default) =>
        _ledger.EnsureUserAccountsAsync(userId, OpenedOnRegistration, cancellationToken);

    /// <summary>
    /// The wallet screen: balances, rates and the first page of history.
    /// </summary>
    /// <remarks>
    /// Opens the accounts first, every time, and the call is cheap because it
    /// is a no-op once they exist. That is deliberate: it is what stops a lost
    /// <c>user.registered</c> event from leaving somebody with a wallet that
    /// says nothing at all. The event makes the accounts appear sooner; this
    /// makes them appear at all.
    /// </remarks>
    public async Task<WalletResponse> ReadAsync(
        string userId, int historyLimit = 20, CancellationToken cancellationToken = default)
    {
        await OpenAccountsAsync(userId, cancellationToken).ConfigureAwait(false);

        var balances = await _ledger.BalancesAsync(userId, cancellationToken).ConfigureAwait(false);
        var history = await _ledger
            .EntriesAsync(userId, historyLimit, cursor: null, cancellationToken)
            .ConfigureAwait(false);

        return new WalletResponse(
            Accounts: [.. balances.Select(b => new AccountDto(b.Currency.Code(), b.Balance, null))],
            Rates: Rates(),
            Transactions: new Platform.Api.CursorPage<TransactionDto>(
                [.. history.Items.Select(Project)],
                history.NextCursor));
    }

    /// <summary>A page of the user's movements.</summary>
    public async Task<Platform.Api.CursorPage<TransactionDto>> HistoryAsync(
        string userId, int limit, string? cursor, CancellationToken cancellationToken = default)
    {
        var page = await _ledger.EntriesAsync(userId, limit, cursor, cancellationToken)
            .ConfigureAwait(false);

        return new Platform.Api.CursorPage<TransactionDto>(
            [.. page.Items.Select(Project)], page.NextCursor);
    }

    /// <summary>
    /// A ledger entry, as the app's list shows it.
    /// </summary>
    /// <remarks>
    /// The sign decides the direction, so one posting reads as
    /// <c>transfer_sent</c> to the payer and <c>transfer_received</c> to the
    /// payee without the ledger having to record it twice.
    /// </remarks>
    private static TransactionDto Project(LedgerEntryView entry)
    {
        var incoming = entry.Amount.MinorUnits > 0;

        var type = entry.Kind switch
        {
            "transfer" => incoming ? LedgerEntryTypes.TransferReceived : LedgerEntryTypes.TransferSent,
            "conversion" => LedgerEntryTypes.Conversion,
            "settlement" => LedgerEntryTypes.Recharge,
            "payment" => LedgerEntryTypes.QrPayment,
            // Unknown to this build. Passed through rather than dropped: the
            // client already tolerates a type it does not know, and a movement
            // missing from a statement is worse than one labelled oddly.
            _ => entry.Kind,
        };

        return new TransactionDto(
            Id: entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Type: type,
            Meta: entry.Metadata,
            Amount: entry.Amount,
            OccurredAt: entry.OccurredAt);
    }

    /// <summary>
    /// Placeholder rates, and knowingly so.
    /// </summary>
    /// <remarks>
    /// Rates belong to the Exchange context, which has contracts and no
    /// implementation yet. Returning parity here is honest for three
    /// dollar-denominated instruments and keeps the wallet response the shape
    /// the client already parses; it is replaced by a call to Exchange the day
    /// Exchange exists, and this comment is the reminder.
    /// </remarks>
    private static Dictionary<string, string> Rates() => new(StringComparer.Ordinal)
    {
        ["USD_USDC"] = "1.0000",
        ["USD_USDT"] = "1.0000",
        ["USDC_USDT"] = "1.0000",
    };
}
