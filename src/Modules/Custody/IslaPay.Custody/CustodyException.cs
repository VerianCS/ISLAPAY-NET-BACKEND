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
        $"This build does not watch '{id}'.");

    public static CustodyException CurrencyNotOnNetwork(
        CustodyNetwork network, string? currency) => new(
        CustodyErrors.CurrencyNotOnNetwork,
        StatusCodes.Unprocessable,
        $"{network.Name} carries {network.Currency.Code()}, not '{currency}'.")
    {
        Meta = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["network"] = network.Id,
            ["currency"] = network.Currency.Code(),
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
