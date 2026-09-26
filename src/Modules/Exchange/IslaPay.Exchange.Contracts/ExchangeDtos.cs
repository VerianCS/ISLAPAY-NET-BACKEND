using IslaPay.Platform;

namespace IslaPay.Exchange.Contracts;

/// <summary><c>POST /v1/exchange/quotes</c>.</summary>
/// <param name="Amount">How much to convert, in the source currency.</param>
/// <param name="To">Target currency code.</param>
public sealed record QuoteRequest(Money Amount, string To);

/// <summary>
/// A priced conversion, valid until <paramref name="ExpiresAt"/>.
/// </summary>
/// <remarks>
/// A quote that cannot be executed is still a <em>successful</em> response —
/// 200 with <c>executable: false</c> and a reason — not an error. It becomes
/// an error only if the client tries to execute it anyway. That distinction
/// matters: the convert sheet asks for a quote on every keystroke, and most
/// "you don't have enough" answers are a normal step in composing an amount,
/// not a failure.
/// </remarks>
/// <param name="QuoteId">Pass to <see cref="ConversionRequest"/> to execute.</param>
/// <param name="From">Source currency code.</param>
/// <param name="To">Target currency code.</param>
/// <param name="Amount">What the user gives, before the fee.</param>
/// <param name="Fee">Taken in the source currency, credited to the settlement fund.</param>
/// <param name="Received">What lands in the target account.</param>
/// <param name="Rate">
/// Units of <paramref name="To"/> per one of <paramref name="From"/>, as a
/// decimal string. A rate is a ratio, not money, so it carries no currency.
/// </param>
/// <param name="ExpiresAt">
/// After this the quote is dead and executing it returns
/// <see cref="QuoteExpired"/>. The client re-quotes in the
/// background before reaching it (D7).
/// </param>
/// <param name="Executable">Whether <see cref="ConversionRequest"/> would succeed now.</param>
/// <param name="Reason">
/// Why not, when <paramref name="Executable"/> is false: an
/// <see cref="ExchangeErrors"/> value, typically
/// <c>WalletErrors.InsufficientFunds</c> or
/// <see cref="FundUnavailable"/>. This is how the client learns
/// about fund solvency now that the fund itself does not travel (D2).
/// </param>
public sealed record QuoteResponse(
    string QuoteId,
    string From,
    string To,
    Money Amount,
    Money Fee,
    Money Received,
    string Rate,
    DateTimeOffset ExpiresAt,
    bool Executable,
    string? Reason = null);

/// <summary>
/// <c>POST /v1/exchange/conversions</c>. Requires an <c>Idempotency-Key</c>.
/// </summary>
/// <remarks>
/// Carries only the quote id. The amounts are whatever the server priced, so
/// there is no way for the client to execute at figures the user was never
/// shown.
/// </remarks>
public sealed record ConversionRequest(string QuoteId);

/// <summary>What <c>POST /v1/exchange/conversions</c> answers: the conversion, as posted.</summary>
/// <param name="PostingId">The ledger posting. The wallet's history shows it as two movements.</param>
/// <param name="Applied">False when this quote had already been converted: nothing moved twice.</param>
public sealed record ConversionReceipt(
    Guid PostingId,
    string From,
    string To,
    Money Amount,
    Money Fee,
    Money Received,
    string Rate,
    DateTimeOffset At,
    bool Applied);

/// <summary>
/// The rates the exchange converts at, for a module that shows them.
/// </summary>
/// <remarks>
/// The wallet screen shows rates beside balances (D1). They used to be a
/// placeholder in Wallet; they are the exchange's, so the exchange says them.
/// </remarks>
public interface IExchangeRates
{
    /// <summary><c>FROM_TO</c> → units of TO per one FROM, as a decimal string.</summary>
    IReadOnlyDictionary<string, string> Rates();
}
