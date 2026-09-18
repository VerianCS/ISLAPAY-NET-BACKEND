using IslaPay.Contracts;

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
public sealed class IdentityException : Exception
{
    public IdentityException(string code, int status, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Status = status;
    }

    /// <summary>One of <see cref="ErrorCodes"/>.</summary>
    public string Code { get; }

    public int Status { get; }

    /// <summary>Extra facts for the problem document's <c>meta</c>.</summary>
    public Dictionary<string, object> Meta { get; } = [];

    public static IdentityException InvalidCredentials() =>
        new(ErrorCodes.InvalidCredentials, 401, "Wrong e-mail or password.");

    public static IdentityException TokenInvalid(string why) =>
        new(ErrorCodes.TokenInvalid, 401, why);

    public static IdentityException Unavailable(string why, Exception? inner = null) =>
        new(ErrorCodes.IdentityUnavailable, 503, why, inner);
}
