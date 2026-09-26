using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IslaPay.Catalog.Contracts;
using IslaPay.Exchange.Contracts;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace IslaPay.Exchange;

/// <summary>What the exchange reads from configuration.</summary>
public sealed class ExchangeOptions
{
    /// <summary>As in Wallet and P2P: off only in tests that do not verify phones.</summary>
    public bool RequireVerifiedPhone { get; init; } = true;
}

/// <summary>
/// Converting between the currencies a customer holds, at one dollar each.
/// </summary>
/// <remarks>
/// <para>
/// E-ISLA, USDT and USDC are all worth a dollar, so the rate is 1 and the
/// only price is the fee: one per cent, taken in the currency given. What
/// makes this the heart of the peg is where the money goes. Converting USDT
/// into E-ISLA puts the USDT in the settlement fund — the reserve the issuer
/// counts — and takes E-ISLA out of it; converting back does the reverse. The
/// fund has to hold what it pays out, so E-ISLA to give away is minted first,
/// and only against reserves.
/// </para>
/// <para>
/// A quote is not stored. It is signed by the server, carries its own expiry
/// and the person it was given to, and comes back as the whole of a
/// conversion request — so a client cannot execute at figures it was never
/// shown, and nothing needs sweeping when quotes are abandoned, which most
/// are: the sheet asks for one on every keystroke.
/// </para>
/// </remarks>
public sealed class ExchangeService : IExchangeRates
{
    /// <summary>One per cent, in basis points, taken from the amount given.</summary>
    public const int FeeBps = 100;

    /// <summary>How long a quote may be executed. The client re-quotes before it runs out.</summary>
    public static readonly TimeSpan QuoteLifetime = TimeSpan.FromSeconds(30);

    private const string Rate = "1.0000";

    private readonly ILedger _ledger;
    private readonly ICurrencyCatalog _catalog;
    private readonly IUserDirectory _directory;
    private readonly IDataProtector _quotes;
    private readonly TimeProvider _clock;
    private readonly ExchangeOptions _options;

    public ExchangeService(
        ILedger ledger, ICurrencyCatalog catalog, IUserDirectory directory,
        IDataProtectionProvider protection, TimeProvider clock, ExchangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(protection);
        _ledger = ledger;
        _catalog = catalog;
        _directory = directory;
        _quotes = protection.CreateProtector("IslaPay.Exchange.Quote.v1");
        _clock = clock;
        _options = options;
    }

    /// <summary>Every pair that converts, as <c>FROM_TO</c> → rate. What the wallet screen shows.</summary>
    public IReadOnlyDictionary<string, string> Rates()
    {
        var codes = Convertible().Select(c => c.Code).ToList();
        var rates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var from in codes)
        {
            foreach (var to in codes.Where(t => t != from))
                rates[$"{from}_{to}"] = Rate;
        }

        return rates;
    }

    public async Task<QuoteResponse> QuoteAsync(
        string userId, QuoteRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(request);

        var (amount, fee, received) = Price(request.Amount, request.To);
        var expires = _clock.GetUtcNow() + QuoteLifetime;
        var reason = await ReasonNotAsync(userId, amount, received, ct).ConfigureAwait(false);

        var payload = JsonSerializer.Serialize(new Sealed(
            userId, amount.Currency.Code, amount.MinorUnits, received.Currency.Code, expires.ToUnixTimeMilliseconds()));

        return new QuoteResponse(
            QuoteId: _quotes.Protect(payload),
            From: amount.Currency.Code,
            To: received.Currency.Code,
            Amount: amount,
            Fee: fee,
            Received: received,
            Rate: Rate,
            ExpiresAt: expires,
            Executable: reason is null,
            Reason: reason);
    }

    public async Task<ConversionReceipt> ConvertAsync(
        string userId, ConversionRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(request);

        var quote = Open(request.QuoteId, userId);
        var from = _catalog.Require(quote.From);
        var (amount, fee, received) = Price(Money.FromMinorUnits(quote.Amount, from), quote.To);
        var key = $"exchange:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.QuoteId)))[..40]}";

        // A retry of a conversion that went through is answered, not refused
        // for having expired in the meantime or for the balance it spent.
        var done = await _ledger.FindPostingAsync(key, ct).ConfigureAwait(false);
        if (done is null)
        {
            if (DateTimeOffset.FromUnixTimeMilliseconds(quote.Expires) <= _clock.GetUtcNow())
            {
                throw new ExchangeException(
                    ExchangeErrors.QuoteExpired, StatusCodes.Status409Conflict,
                    "The quote has expired. Ask for a new one.");
            }

            if (await ReasonNotAsync(userId, amount, received, ct).ConfigureAwait(false) is { } reason)
                throw Refusal(reason, reason == ExchangeErrors.FundUnavailable ? received : amount);
        }

        var principal = amount - fee;
        List<PostingLeg> legs =
        [
            new(AccountRef.User(userId, amount.Currency), -amount),
            new(AccountRef.SettlementFund(amount.Currency), principal),
            new(AccountRef.SettlementFund(received.Currency), -received),
            new(AccountRef.User(userId, received.Currency), received),
        ];
        // Omitted when it rounds to nothing: a leg that moves nothing is refused.
        if (fee.IsPositive) legs.Insert(2, new PostingLeg(AccountRef.Fees(amount.Currency), fee));

        PostingReceipt posted;
        try
        {
            posted = await _ledger.PostAsync(new PostingRequest(
                Kind: "conversion",
                Legs: legs,
                IdempotencyKey: key,
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["from"] = amount.Currency.Code,
                    ["to"] = received.Currency.Code,
                    ["rate"] = Rate,
                    ["feeBps"] = FeeBps.ToString(CultureInfo.InvariantCulture),
                    ["fee"] = fee.ToString(),
                    ["received"] = received.ToString(),
                }), ct).ConfigureAwait(false);
        }
        catch (InsufficientFundsException)
        {
            throw Refusal("insufficient_funds", amount);
        }

        return new ConversionReceipt(
            posted.PostingId, amount.Currency.Code, received.Currency.Code,
            amount, fee, received, Rate, _clock.GetUtcNow(), posted.Written);
    }

    /// <summary>The amount, the fee on it and what arrives, or why the pair cannot be priced.</summary>
    private (Money Amount, Money Fee, Money Received) Price(Money given, string? toCode)
    {
        var convertible = Convertible();
        var from = convertible.FirstOrDefault(c => c.Code == given.Currency.Code);
        var to = convertible.FirstOrDefault(c => string.Equals(c.Code, toCode?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (from is null || to is null || from.Code == to.Code)
        {
            throw new ExchangeException(
                ExchangeErrors.PairUnavailable, StatusCodes.Status422UnprocessableEntity,
                $"{given.Currency.Code} cannot be converted to {toCode}.");
        }

        var amount = Money.FromMinorUnits(given.MinorUnits, from.Currency);
        if (!amount.IsPositive)
        {
            throw new ExchangeException(
                ExchangeErrors.InvalidAmount, StatusCodes.Status422UnprocessableEntity,
                "Converting takes a positive amount.");
        }

        var fee = amount.MultiplyByBasisPoints(FeeBps, MidpointRounding.ToEven);
        // Down, never up: rounding up would pay out a fraction the fund never
        // received. What is below the target's last decimal stays in the fund.
        var received = (amount - fee).ConvertTo(to.Currency, 1m, MidpointRounding.ToZero);
        if (!received.IsPositive)
        {
            throw new ExchangeException(
                ExchangeErrors.InvalidAmount, StatusCodes.Status422UnprocessableEntity,
                $"{amount} is too small to receive anything in {to.Code}.");
        }

        return (amount, fee, received);
    }

    /// <summary>Why this conversion cannot happen now, as a code, or null if it can.</summary>
    private async Task<string?> ReasonNotAsync(string userId, Money amount, Money received, CancellationToken ct)
    {
        var user = await _directory.FindByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return ExchangeErrors.QuoteInvalid;
        if (_options.RequireVerifiedPhone && !user.PhoneVerified) return "phone_not_verified";
        // Frozen, yes; the per-movement limit, no. Converting moves nothing out
        // of the person's hands — the limit is met when the money leaves.
        if (Standing.Check(user) is { } refusal) return refusal.Code;

        var held = await _ledger.BalanceOfAsync(AccountRef.User(userId, amount.Currency), ct).ConfigureAwait(false);
        if (held.MinorUnits < amount.MinorUnits) return "insufficient_funds";

        var fund = await _ledger.BalanceOfAsync(AccountRef.SettlementFund(received.Currency), ct).ConfigureAwait(false);
        if (fund.MinorUnits < received.MinorUnits) return ExchangeErrors.FundUnavailable;

        return null;
    }

    private Sealed Open(string? quoteId, string userId)
    {
        Sealed? quote = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(quoteId))
                quote = JsonSerializer.Deserialize<Sealed>(_quotes.Unprotect(quoteId));
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            quote = null;
        }

        // Not ours, altered, or somebody else's: the same answer, because
        // telling them apart would only help whoever is trying.
        if (quote is null || !string.Equals(quote.User, userId, StringComparison.Ordinal))
        {
            throw new ExchangeException(
                ExchangeErrors.QuoteInvalid, StatusCodes.Status422UnprocessableEntity,
                "That quote was not issued to this account.");
        }

        return quote;
    }

    private static ExchangeException Refusal(string code, Money about)
    {
        var status = code switch
        {
            "phone_not_verified" or Standing.AccountFrozen => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        var failure = new ExchangeException(code, status, $"The conversion cannot go ahead: {code}.");
        failure.Facts["currency"] = about.Currency.Code;
        return failure;
    }

    /// <summary>What a customer holds and may convert: switched on, and holdable.</summary>
    private List<CurrencyInfo> Convertible() =>
        [.. _catalog.Currencies.Where(c => c.Enabled && c.CustomerHoldable)];

    /// <summary>What a quote id carries, signed.</summary>
    private sealed record Sealed(string User, string From, long Amount, string To, long Expires);
}
