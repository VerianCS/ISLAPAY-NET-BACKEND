using IslaPay.Identity.Contracts;
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
    private readonly IUserDirectory _directory;
    private readonly WalletOptions _options;

    public WalletService(ILedger ledger, IUserDirectory directory, WalletOptions options)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(options);
        _ledger = ledger;
        _directory = directory;
        _options = options;
    }

    /// <summary>
    /// Moves money from one IslaPay account to another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Internal transfers only: the destination is an e-mail that belongs to
    /// an account here. Sending to a chain address is a withdrawal, which
    /// needs a custodian and settles on someone else’s schedule — a different
    /// operation wearing the same word.
    /// </para>
    /// <para>
    /// No fee. The contract charges 1% on conversions and P2P, and nothing on
    /// moving your own money between IslaPay accounts.
    /// </para>
    /// </remarks>
    /// <param name="idempotencyKey">
    /// Passed down to the ledger as well as being honoured at the HTTP layer.
    /// Both, because they fail differently: if the posting commits and the
    /// response is then lost to a 5xx, the HTTP claim is released so the client
    /// can retry — and the ledger’s copy of the key is what stops that retry
    /// from moving the money a second time.
    /// </param>
    public async Task<TransactionDto> TransferAsync(
        string senderId,
        TransferRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderId);

        if (request.Amount.MinorUnits <= 0)
        {
            throw new WalletException(
                WalletErrors.InvalidAmount, 422, "A transfer must be for more than zero.");
        }

        var destination = request.Destination?.Trim() ?? string.Empty;
        if (destination.Length == 0)
        {
            throw new WalletException(
                WalletErrors.MissingDestination, 422, "A transfer needs a destination.");
        }

        var sender = await _directory.FindByIdAsync(senderId, cancellationToken).ConfigureAwait(false)
            ?? throw new WalletException(
                WalletErrors.MissingDestination, 422, "The sender’s account no longer exists.");

        if (_options.RequireVerifiedPhone && !sender.PhoneVerified)
        {
            throw new WalletException(
                WalletErrors.PhoneNotVerified, 403,
                "The account must prove its phone number before money can move.");
        }

        var recipient = await _directory.FindByEmailAsync(destination, cancellationToken)
            .ConfigureAwait(false);

        // Deliberately the same answer as an address that is not an e-mail at
        // all. Distinguishing them would turn this endpoint into a way to ask
        // which addresses have accounts.
        if (recipient is null)
        {
            throw new WalletException(
                WalletErrors.MissingDestination, 422,
                "No IslaPay account is registered to that address.");
        }

        if (string.Equals(recipient.UserId, senderId, StringComparison.Ordinal))
        {
            throw new WalletException(
                WalletErrors.SelfTransfer, 422, "A transfer cannot be addressed to the sender.");
        }

        var currency = request.Amount.Currency;

        // Named here rather than by the ledger, because the event published
        // with the posting has to refer to it.
        var postingId = Guid.NewGuid();

        var posting = new PostingRequest(
            Kind: "transfer",
            Legs:
            [
                new PostingLeg(AccountRef.User(senderId, currency), -request.Amount),
                new PostingLeg(AccountRef.User(recipient.UserId, currency), request.Amount),
            ],
            // Qualified with the operation and the sender. The ledger's key is
            // unique across the whole ledger, and the client's key is only
            // unique to that client — two users picking the same one would
            // collide, and the second would be told it worked.
            IdempotencyKey: $"transfer:{senderId}:{idempotencyKey}",
            // Both sides are recorded on the posting, because one posting is
            // read by two people and each wants to see the other. The client
            // picks whichever is not them.
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["from"] = sender.Email,
                ["to"] = recipient.Email,
                ["fromName"] = sender.Name,
                ["toName"] = recipient.Name,
                ["note"] = request.Note?.Trim() ?? string.Empty,
            },
            Events:
            [
                new PendingEvent(
                    WalletEvents.Context,
                    WalletEvents.TransferCompleted,
                    new TransferCompleted(
                        postingId, senderId, recipient.UserId, request.Amount,
                        DateTimeOffset.UtcNow)),
            ],
            PostingId: postingId);

        try
        {
            await _ledger.PostAsync(posting, cancellationToken).ConfigureAwait(false);
        }
        catch (InsufficientFundsException e)
        {
            var failure = new WalletException(
                WalletErrors.InsufficientFunds, 422,
                $"The account holds {e.Available} and {e.Requested} was requested.");

            // The client’s InsufficientFunds(currency) cannot be constructed
            // without these — see API_CONTRACT.md §4.
            failure.Facts["currency"] = currency.Code();
            failure.Facts["available"] = e.Available.ToString();
            failure.Facts["requested"] = e.Requested.ToString();
            throw failure;
        }

        // Read back rather than constructed, so what the client is handed is
        // what the history will show it next time.
        var page = await _ledger.EntriesAsync(senderId, 1, cursor: null, cancellationToken)
            .ConfigureAwait(false);

        return Project(page.Items[0]);
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
