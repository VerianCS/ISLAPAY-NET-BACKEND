using IslaPay.Identity.Contracts;

namespace IslaPay.Identity;

/// <inheritdoc />
/// <remarks>
/// A thin projection of <see cref="IAdminClient"/> down to the four fields
/// another module may see. The admin client itself stays internal: it can
/// create accounts and reset passwords, and nothing outside this module should
/// be able to reach it.
/// </remarks>
internal sealed class KeycloakUserDirectory : IUserDirectory
{
    private readonly IAdminClient _admin;

    public KeycloakUserDirectory(IAdminClient admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        _admin = admin;
    }

    public async Task<DirectoryUser?> FindByEmailAsync(
        string email, CancellationToken cancellationToken = default)
    {
        // Lower-cased and trimmed, because that is how registration stored it.
        var normalised = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (normalised.Length == 0) return null;

        var user = await _admin.FindByEmailAsync(normalised, cancellationToken).ConfigureAwait(false);
        return user is null ? null : Describe(user);
    }

    public async Task<DirectoryUser?> FindByIdAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        try
        {
            return Describe(await _admin.GetAsync(userId, cancellationToken).ConfigureAwait(false));
        }
        catch (IdentityException)
        {
            // The account named by a token no longer exists. Null, because the
            // caller asked who this is and the answer is nobody.
            return null;
        }
    }

    private static DirectoryUser Describe(KeycloakUser user) =>
        new(user.Id, user.Email, user.DisplayName, user.PhoneVerified, user.IdentityVerified, user.Frozen);
}
