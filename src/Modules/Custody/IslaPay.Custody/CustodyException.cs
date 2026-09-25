using IslaPay.Custody.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Api;

namespace IslaPay.Custody;

/// <summary>A refusal this module raises, translated by the platform.</summary>
public sealed class CustodyException : Exception, IslaPay.Platform.Api.IApiFailure
{
    public CustodyException(string code, int status, string message)
        : base(message)
    {
        Code = code;
        Status = status;
    }

    public string Code { get; } = string.Empty;

    public int Status { get; }

    public IReadOnlyDictionary<string, object>? Meta { get; init; }

    public static CustodyException UnknownNetwork(string? id) => new(
        CustodyErrors.UnknownNetwork,
        StatusCodes.NotFound,
        $"'{id}' is not a chain this build watches.");

    public static CustodyException CurrencyNotOnNetwork(
        string? currency, string? network) => new(
        CustodyErrors.CurrencyNotOnNetwork,
        StatusCodes.Unprocessable,
        $"'{currency}' is not available on '{network}'.")
        {
            Meta = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["network"] = network ?? string.Empty,
                ["currency"] = currency ?? string.Empty,
            },
        };

    public static CustodyException AddressUnavailable(string detail) => new(
        CustodyErrors.AddressUnavailable, StatusCodes.Unavailable, detail);

    public static CustodyException PhoneNotVerified() => new(
        CustodyErrors.PhoneNotVerified,
        StatusCodes.Forbidden,
        "Prove your phone number before receiving money.");

    /// <summary>Named rather than typed out at each call site.</summary>
    private static class StatusCodes
    {
        public const int Forbidden = 403;
        public const int NotFound = 404;
        public const int Unprocessable = 422;
        public const int Unavailable = 503;
    }
}
