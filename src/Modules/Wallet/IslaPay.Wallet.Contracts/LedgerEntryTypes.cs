namespace IslaPay.Wallet.Contracts;

/// <summary>
/// The kinds of movement a wallet history can contain.
/// </summary>
/// <remarks>
/// <para>
/// Strings rather than an enum, for the same reason as
/// <see cref="WalletErrors"/>: the client renders a type it knows and falls back
/// to a generic line for one it does not, so a new movement kind does not need
/// an app release and never breaks deserialisation.
/// </para>
/// <para>
/// This set is derived from what the client actually produces today. Each
/// entry lists the <c>meta</c> keys the client needs to compose its sentence —
/// omitting one degrades the line, so treat them as required.
/// </para>
/// <para>
/// Note the fee: today it is baked into the rendered title
/// (<c>"Conversión USD → USDT (comisión 1.00 USD)"</c>). As a <c>meta</c>
/// field it becomes a real amount the client can format, align and translate.
/// </para>
/// </remarks>
public static class LedgerEntryTypes
{
    /// <summary>Money out. <c>meta</c>: <c>destination</c>.</summary>
    public const string TransferSent = "transfer_sent";

    /// <summary>Money in. <c>meta</c>: <c>counterparty</c>.</summary>
    public const string TransferReceived = "transfer_received";

    /// <summary>Money out. <c>meta</c>: <c>from</c>, <c>to</c>, <c>fee</c>, <c>received</c>.</summary>
    public const string Conversion = "conversion";

    /// <summary>Money in. <c>meta</c>: <c>method</c>.</summary>
    public const string Recharge = "recharge";

    /// <summary>Money out. <c>meta</c>: <c>service</c>, <c>provider</c>.</summary>
    public const string BillPayment = "bill_payment";

    /// <summary>Money out. <c>meta</c>: <c>merchant</c>.</summary>
    public const string QrPayment = "qr_payment";

    /// <summary>Money out. <c>meta</c>: <c>merchant</c>, <c>orderId</c>.</summary>
    public const string StorePurchase = "store_purchase";

    /// <summary>Money out. <c>meta</c>: <c>method</c>, <c>fee</c>.</summary>
    public const string P2PSell = "p2p_sell";

    /// <summary>Money in. <c>meta</c>: <c>method</c>, <c>fee</c>.</summary>
    public const string P2PBuy = "p2p_buy";

    /// <summary>Every type above. For validation and tests, not for dispatch.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            TransferSent,
            TransferReceived,
            Conversion,
            Recharge,
            BillPayment,
            QrPayment,
            StorePurchase,
            P2PSell,
            P2PBuy,
        };
}
