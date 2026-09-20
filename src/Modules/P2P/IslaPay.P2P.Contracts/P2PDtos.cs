using IslaPay.Platform;

namespace IslaPay.P2P.Contracts;

/// <summary>Which way the user is trading.</summary>
public enum P2PSide
{
    /// <summary>User gives the wallet currency and receives local money.</summary>
    Sell,

    /// <summary>User pays in local money and receives the wallet currency.</summary>
    Buy,
}

/// <summary>
/// A payment rail the instant-exchange market accepts.
/// </summary>
/// <param name="Id">Stable identifier, sent back on a trade.</param>
/// <param name="Name">Display name, e.g. <c>CUP Transfermóvil</c>.</param>
/// <param name="Code">Local currency code shown on the avatar, e.g. <c>CUP</c>.</param>
/// <param name="Rate">
/// Units of <paramref name="Code"/> per one wallet unit, as a decimal string.
/// <c>"1"</c> means parity — the client says so in words rather than printing
/// "1 USD ≈ 1 USD", which tells the user nothing.
/// </param>
/// <param name="Available">
/// Whether the rail can be traded right now. A rail that is temporarily down
/// stays in the list, greyed, rather than vanishing — a method disappearing
/// without explanation reads as a bug to the user.
/// </param>
public sealed record P2PMethodDto(
    string Id,
    string Name,
    string Code,
    string Rate,
    bool Available = true);

/// <summary>
/// <c>POST /v1/p2p/trades</c>. Requires an <c>Idempotency-Key</c>.
/// </summary>
/// <remarks>
/// This is instant exchange against IslaPay, not a user-to-user market: there
/// is no counterparty, no escrow and no dispute, because IslaPay is the other
/// side at a published rate (D6). A peer market is a later, separate surface.
/// </remarks>
/// <param name="Side">Sell or buy.</param>
/// <param name="Amount">Amount in the wallet currency, before the fee.</param>
/// <param name="MethodId">The rail, from <see cref="P2PMethodDto.Id"/>.</param>
public sealed record P2PTradeRequest(
    P2PSide Side,
    Money Amount,
    string MethodId);
