namespace IslaPay.Identity.Contracts;

/// <summary>
/// The events Identity publishes, and their routing keys.
/// </summary>
/// <remarks>
/// Part of the module's public face, exactly like its DTOs: another module
/// binds to these keys and deserialises these shapes, so changing one breaks a
/// consumer as surely as changing a response body breaks the app. The version
/// suffix is what makes a breaking change possible — publish <c>.v2</c>
/// alongside <c>.v1</c> and retire the old key when nothing is bound to it.
/// </remarks>
public static class IdentityEvents
{
    /// <summary>The bounded context, and therefore the exchange <c>x.identity</c>.</summary>
    public const string Context = "identity";

    /// <summary>Everything Identity publishes matches this.</summary>
    public const string AllUserEvents = "user.*.v1";

    /// <summary>An account was created. Routing key <c>user.registered.v1</c>.</summary>
    public const string UserRegistered = "user.registered.v1";
}

/// <summary>
/// An account now exists.
/// </summary>
/// <remarks>
/// <para>
/// Carries what a consumer plausibly needs rather than the whole user: an
/// event that duplicates another context's state turns every consumer into a
/// second copy of it, out of date from the moment it is written.
/// </para>
/// <para>
/// Note what is <em>not</em> promised here. Keycloak owns the account, and
/// this database cannot take part in the transaction that created it, so the
/// event can be lost between the two. A consumer that must not miss a user
/// therefore needs a way to catch up on its own — see Wallet, which opens
/// accounts on first read as well as on this event.
/// </para>
/// </remarks>
/// <param name="UserId">Keycloak's subject. The same value the access token carries.</param>
public sealed record UserRegistered(
    string UserId,
    string Email,
    string? Phone,
    DateTimeOffset RegisteredAt);
