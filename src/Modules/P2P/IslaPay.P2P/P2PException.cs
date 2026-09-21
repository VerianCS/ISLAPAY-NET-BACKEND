using IslaPay.Platform.Api;

namespace IslaPay.P2P;

/// <summary>
/// A P2P failure the platform can turn into a response without knowing
/// anything about P2P.
/// </summary>
/// <remarks>
/// <see cref="Facts"/> is what reaches the problem document's <c>meta</c>.
/// Some codes cannot be used without it — see
/// <c>P2PErrors.RequireCurrencyMeta</c>.
/// </remarks>
public sealed class P2PException : Exception, IApiFailure
{
    public P2PException(string code, int status, string message) : base(message)
    {
        Code = code;
        Status = status;
    }

    public string Code { get; }

    public int Status { get; }

    public Dictionary<string, object> Facts { get; } = [];

    IReadOnlyDictionary<string, object>? IApiFailure.Meta => Facts.Count > 0 ? Facts : null;
}
