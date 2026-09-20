using IslaPay.Platform;
using IslaPay.Platform.Api;

namespace IslaPay.Wallet.Contracts;

/// <summary>
/// <c>GET /v1/me/wallet</c> — balances, rates and recent activity in one
/// response.
/// </summary>
/// <remarks>
/// Composed by the gateway from Ledger, Exchange and Wallet. The client opens
/// on a screen that needs all three, and three round trips on a poor mobile
/// connection is three chances to fail. See <c>API_CONTRACT.md</c> D1.
/// <para>
/// The settlement fund is deliberately absent: solvency is decided server-side
/// and reported on the quote (D2).
/// </para>
/// </remarks>
/// <param name="Accounts">One per currency the user holds.</param>
/// <param name="Rates">
/// Units of the second currency per one of the first, keyed <c>FROM_TO</c>
/// (for example <c>USD_USDT</c>). A decimal string, like every other number
/// that matters.
/// </param>
/// <param name="Transactions">The first page of history, newest first.</param>
public sealed record WalletResponse(
    IReadOnlyList<AccountDto> Accounts,
    IReadOnlyDictionary<string, string> Rates,
    CursorPage<TransactionDto> Transactions);

/// <param name="Currency">Wire code: <c>USD</c>, <c>USDC</c> or <c>USDT</c>.</param>
/// <param name="Balance">Current balance. Never negative for a wallet account.</param>
/// <param name="CardLast4">
/// Last four digits of the card bound to this account, for display only.
/// </param>
public sealed record AccountDto(
    string Currency,
    Money Balance,
    // Null until the account has a card, and omitted from the response when it
    // is. An account is opened the moment someone registers and a card comes
    // later, so the alternative was an empty string or four invented digits —
    // both of which the client would have to know to disbelieve.
    string? CardLast4);

/// <summary>One line of wallet history.</summary>
/// <param name="Id">Stable identifier, usable as a pagination cursor anchor.</param>
/// <param name="Type">
/// One of <see cref="LedgerEntryTypes"/>. The client composes the visible
/// sentence from this plus <paramref name="Meta"/> — the server never sends
/// rendered copy, so history is translated like everything else (D3).
/// </param>
/// <param name="Meta">
/// The nouns the client substitutes into its template. Keys depend on
/// <paramref name="Type"/>; see <see cref="LedgerEntryTypes"/>.
/// </param>
/// <param name="Amount">
/// Signed: positive is money in, negative is money out. This matches the
/// client's existing ledger convention.
/// </param>
/// <param name="OccurredAt">When it happened, UTC.</param>
public sealed record TransactionDto(
    string Id,
    string Type,
    IReadOnlyDictionary<string, string> Meta,
    Money Amount,
    DateTimeOffset OccurredAt);

/// <summary><c>POST /v1/transfers</c>. Requires an <c>Idempotency-Key</c>.</summary>
/// <param name="Amount">What to send.</param>
/// <param name="Destination">
/// An e-mail for an internal transfer, or an on-chain address.
/// </param>
/// <param name="Network">
/// Required when the currency is on-chain; ignored for USD. One of the five
/// networks in <c>API_CONTRACT.md</c> §2, sent verbatim.
/// </param>
/// <param name="Note">Optional free text the sender attaches.</param>
public sealed record TransferRequest(
    Money Amount,
    string Destination,
    string? Network = null,
    string? Note = null);

/// <summary><c>POST /v1/recharges</c>. Requires an <c>Idempotency-Key</c>.</summary>
public sealed record RechargeRequest(Money Amount, string Method);

/// <summary>
/// <c>GET /v1/deposit-addresses</c> — a persistent address per user and
/// network (D4).
/// </summary>
/// <param name="Currency">The currency this address receives.</param>
/// <param name="Network">The network it lives on.</param>
/// <param name="Address">What the client shows and renders as a QR.</param>
/// <param name="Memo">
/// Networks that need a memo or destination tag to credit the deposit. When
/// present the client must show it as prominently as the address: a deposit
/// sent without it is not automatically recoverable.
/// </param>
public sealed record DepositAddressResponse(
    string Currency,
    string Network,
    string Address,
    string? Memo = null);

/// <summary>The result of a movement: what was recorded, and the new balance.</summary>
/// <param name="Entry">The history line this produced.</param>
/// <param name="Balance">
/// The affected account's balance afterwards, so the client does not have to
/// re-fetch the wallet to update the screen.
/// </param>
public sealed record MovementResponse(TransactionDto Entry, Money Balance);
