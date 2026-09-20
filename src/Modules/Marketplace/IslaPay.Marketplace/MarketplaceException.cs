using IslaPay.Platform.Api;

namespace IslaPay.Marketplace;

/// <summary>
/// A Marketplace failure the platform can turn into a response without knowing
/// anything about Marketplace.
/// </summary>
/// <remarks>
/// <see cref="Facts"/> is what reaches the problem document's <c>meta</c>.
/// Some codes cannot be used without it — see
/// <c>MarketplaceErrors.RequireCurrencyMeta</c>.
/// </remarks>
public sealed class MarketplaceException : Exception, IApiFailure
{
    public MarketplaceException(string code, int status, string message) : base(message)
    {
        Code = code;
        Status = status;
    }

    public string Code { get; }

    public int Status { get; }

    public Dictionary<string, object> Facts { get; } = [];

    IReadOnlyDictionary<string, object>? IApiFailure.Meta => Facts.Count > 0 ? Facts : null;
}
