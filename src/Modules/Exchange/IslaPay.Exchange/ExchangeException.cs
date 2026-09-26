using IslaPay.Platform.Api;

namespace IslaPay.Exchange;

/// <summary>A refusal the platform can translate without knowing this module.</summary>
public sealed class ExchangeException : Exception, IApiFailure
{
    public ExchangeException(string code, int status, string message) : base(message)
    {
        Code = code;
        Status = status;
    }

    public string Code { get; }

    public int Status { get; }

    public Dictionary<string, object> Facts { get; } = [];

    IReadOnlyDictionary<string, object>? IApiFailure.Meta => Facts.Count > 0 ? Facts : null;
}
