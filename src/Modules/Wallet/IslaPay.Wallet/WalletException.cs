using IslaPay.Platform.Api;

namespace IslaPay.Wallet;

/// <summary>
/// A Wallet failure the platform can turn into a response without knowing
/// anything about Wallet.
/// </summary>
/// <remarks>
/// <see cref="Facts"/> is what reaches the problem document's <c>meta</c>.
/// Some codes cannot be used without it: the client models
/// <c>InsufficientFunds(currency)</c> as a type that needs a currency, so a
/// response missing one cannot be mapped to a failure it can show.
/// </remarks>
public sealed class WalletException : Exception, IApiFailure
{
    public WalletException(string code, int status, string message) : base(message)
    {
        Code = code;
        Status = status;
    }

    public string Code { get; }

    public int Status { get; }

    public Dictionary<string, object> Facts { get; } = [];

    IReadOnlyDictionary<string, object>? IApiFailure.Meta => Facts.Count > 0 ? Facts : null;
}
