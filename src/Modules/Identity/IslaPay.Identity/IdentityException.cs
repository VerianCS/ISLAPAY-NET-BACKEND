using IslaPay.Identity.Contracts;
using IslaPay.Platform.Api;

namespace IslaPay.Identity;

/// <summary>
/// A failure that already knows which contract error code it is.
/// </summary>
/// <remarks>
/// Throwing this rather than returning a result type is a deliberate choice
/// for this layer: every one of these is a dead end for the request, there is
/// exactly one place that turns them into responses, and the alternative —
/// threading a result through six call sites — makes the happy path harder to
/// read without making any of them harder to get wrong.
/// <para>
/// The message is for logs. The client is shown <see cref="Code"/>, and writes
/// its own copy.
/// </para>
/// </remarks>
public sealed class IdentityException : Exception, IApiFailure
{
    public IdentityException(string code, int status, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Status = status;
    }

    /// <summary>One of <see cref="IdentityErrors"/>, or a platform code.</summary>
    public string Code { get; }

    public int Status { get; }

    /// <summary>Extra facts for the problem document's <c>meta</c>.</summary>
    public Dictionary<string, object> Meta { get; } = [];

    /// <summary>
    /// How the platform's error middleware reads this without knowing what an
    /// <see cref="IdentityException"/> is. Empty becomes null so that an
    /// absent <c>meta</c> is omitted from the document rather than sent as
    /// <c>{}</c>.
    /// </summary>
    IReadOnlyDictionary<string, object>? IApiFailure.Meta =>
        Meta.Count > 0 ? Meta : null;

    public static IdentityException InvalidCredentials() =>
        new(IdentityErrors.InvalidCredentials, 401, "Wrong e-mail or password.");

    public static IdentityException TokenInvalid(string why) =>
        new(PlatformErrors.TokenInvalid, 401, why);

    public static IdentityException Unavailable(string why, Exception? inner = null) =>
        new(IdentityErrors.IdentityUnavailable, 503, why, inner);
}
