namespace IslaPay.Identity.Contracts;

/// <summary>
/// As much of an account as another module is allowed to know.
/// </summary>
/// <remarks>
/// Deliberately not the whole user. A module that can read every field ends up
/// storing a copy of them, and the copy is wrong from the moment someone
/// changes their name.
/// </remarks>
/// <param name="UserId">Keycloak's subject — the value an access token carries.</param>
/// <param name="PhoneVerified">
/// Whether the account has proved its phone number. Money movement is gated on
/// this, so it is read here rather than trusted from a token claim: a token
/// issued before verification keeps saying false until it expires.
/// </param>
public sealed record DirectoryUser(
    string UserId,
    string Email,
    string Name,
    bool PhoneVerified);

/// <summary>
/// Looking a user up, for modules that need to name one.
/// </summary>
/// <remarks>
/// <para>
/// The second way a module reaches Identity, and the only synchronous one. It
/// exists because a transfer is addressed to a person, and the address a human
/// types is an e-mail rather than a subject claim.
/// </para>
/// <para>
/// Read-only, on purpose. Nothing outside Identity creates, disables or edits
/// an account: there is exactly one place that writes to the identity
/// provider, and this is not it.
/// </para>
/// </remarks>
public interface IUserDirectory
{
    /// <summary>
    /// Finds an account by e-mail, or null.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception: "no such user" is an ordinary answer to
    /// "who is this address", and a caller that has to catch to learn it will
    /// eventually catch too much.
    /// </remarks>
    Task<DirectoryUser?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);

    Task<DirectoryUser?> FindByIdAsync(string userId, CancellationToken cancellationToken = default);
}
