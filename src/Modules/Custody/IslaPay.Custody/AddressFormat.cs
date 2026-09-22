using System.Text.RegularExpressions;
using IslaPay.Catalog.Contracts;
using IslaPay.Custody.Contracts;

namespace IslaPay.Custody;

/// <summary>The network's own format, checked before an address is stored.</summary>
internal static class AddressFormat
{
    public static bool Matches(CurrencyOnNetwork network, string? address) =>
        !string.IsNullOrWhiteSpace(address)
        && Regex.IsMatch(
            address,
            network.AddressPattern,
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
}
