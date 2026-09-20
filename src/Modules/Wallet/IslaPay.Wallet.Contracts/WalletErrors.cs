namespace IslaPay.Wallet.Contracts;

/// <summary>Error codes the Wallet module raises.</summary>
/// <remarks>
/// The first six correspond one-to-one with the sealed failure types the
/// Flutter client already models (<c>WalletFailure</c> and <c>CardFailure</c>).
/// Keep them in step: see <c>API_CONTRACT.md</c> §4.
/// </remarks>
public static class WalletErrors
{
    /// <summary>Zero, negative, or unparseable. Client: <c>InvalidAmount</c>.</summary>
    public const string InvalidAmount = "invalid_amount";

    /// <summary>
    /// The user's balance does not cover it. Client:
    /// <c>InsufficientFunds(currency)</c> — so <c>meta.currency</c> is required.
    /// </summary>
    public const string InsufficientFunds = "insufficient_funds";

    /// <summary>No destination address or e-mail. Client: <c>MissingDestination</c>.</summary>
    public const string MissingDestination = "missing_destination";

    /// <summary>
    /// A card of that currency and kind already exists. Client:
    /// <c>DuplicateCard(currency, kind)</c> — <c>meta.currency</c> and
    /// <c>meta.kind</c> required.
    /// </summary>
    public const string DuplicateCard = "duplicate_card";

    /// <summary>Client: <c>MissingHolderName</c>.</summary>
    public const string MissingHolderName = "missing_holder_name";

    /// <summary>Client: <c>MissingDeliveryAddress</c>.</summary>
    public const string MissingDeliveryAddress = "missing_delivery_address";

    /// <summary>
    /// This module's codes whose <c>meta</c> must carry a <c>currency</c>,
    /// because the client's corresponding type cannot be constructed without
    /// one.
    /// </summary>
    /// <remarks>
    /// Declared per module rather than as one global set. Each module knows
    /// which of its own codes carry the obligation, and nobody has to edit a
    /// shared list to add a code.
    /// </remarks>
    public static IReadOnlySet<string> RequireCurrencyMeta { get; } =
        new HashSet<string>(StringComparer.Ordinal) { InsufficientFunds, DuplicateCard };
}
