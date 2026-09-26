using System.Globalization;
using System.Security.Claims;
using IslaPay.Identity.Contracts;
using IslaPay.Platform.AspNet.Security;
using Microsoft.AspNetCore.Http;

namespace IslaPay.Identity;

/// <summary>
/// Freezing an account, and saying how far it has been verified.
/// </summary>
/// <remarks>
/// <para>
/// Both are marks on the account in the identity provider, beside the phone's.
/// Every module that moves a customer's money already reads the account
/// through <see cref="IUserDirectory"/> before it does, so a mark written here
/// is obeyed on the next request everywhere, with no second copy to fall out
/// of step and no event to be missed.
/// </para>
/// <para>
/// A freeze stops money moving; it does not stop the person signing in. They
/// can still see their balance and their history, and read why, which is what
/// they will ask support about.
/// </para>
/// </remarks>
public sealed class ComplianceService
{
    private readonly IAdminClient _admin;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;

    public ComplianceService(IAdminClient admin, IAuditLog audit, TimeProvider clock)
    {
        _admin = admin;
        _audit = audit;
        _clock = clock;
    }

    public async Task<AccountStandingDto> FindAsync(string email, CancellationToken ct = default)
    {
        var user = await _admin.FindByEmailAsync((email ?? string.Empty).Trim().ToLowerInvariant(), ct)
            .ConfigureAwait(false);
        return user is null ? throw NotFound() : Describe(user);
    }

    public async Task<AccountStandingDto> GetAsync(string userId, CancellationToken ct = default) =>
        Describe(await LoadAsync(userId, ct).ConfigureAwait(false));

    public async Task<AccountStandingDto> FreezeAsync(
        HttpContext context, string userId, StandingChangeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reason = RequireReason(request?.Reason);
        await LoadAsync(userId, ct).ConfigureAwait(false);

        await _admin.SetAttributesAsync(userId, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [KeycloakUser.FrozenAttribute] = "true",
            [KeycloakUser.FrozenReasonAttribute] = reason,
            [KeycloakUser.FrozenAtAttribute] = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            [KeycloakUser.FrozenByAttribute] = Actor(context.User),
        }, ct).ConfigureAwait(false);

        return await RecordAsync(context, "compliance.account.frozen", userId, reason, ct).ConfigureAwait(false);
    }

    public async Task<AccountStandingDto> UnfreezeAsync(
        HttpContext context, string userId, StandingChangeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reason = RequireReason(request?.Reason);
        await LoadAsync(userId, ct).ConfigureAwait(false);

        // The marks go; the reason for the freeze and for lifting it stay in
        // the audit log, which is where history belongs.
        await _admin.SetAttributesAsync(userId, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [KeycloakUser.FrozenAttribute] = null,
            [KeycloakUser.FrozenReasonAttribute] = null,
            [KeycloakUser.FrozenAtAttribute] = null,
            [KeycloakUser.FrozenByAttribute] = null,
        }, ct).ConfigureAwait(false);

        return await RecordAsync(context, "compliance.account.unfrozen", userId, reason, ct).ConfigureAwait(false);
    }

    public async Task<AccountStandingDto> SetLevelAsync(
        HttpContext context, string userId, LevelChangeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var reason = RequireReason(request.Reason);
        var user = await LoadAsync(userId, ct).ConfigureAwait(false);

        if (request.Level is not (Standing.PhoneVerified or Standing.IdentityVerified))
        {
            throw new IdentityException(
                IdentityErrors.InvalidStandingChange, StatusCodes.Status422UnprocessableEntity,
                "the level can be set to 1 (phone) or 2 (identity checked).");
        }

        if (request.Level == Standing.IdentityVerified && !user.PhoneVerified)
        {
            throw new IdentityException(
                IdentityErrors.InvalidStandingChange, StatusCodes.Status422UnprocessableEntity,
                "an account proves its phone before its identity is marked as checked.");
        }

        await _admin.SetAttributesAsync(userId, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [KeycloakUser.IdentityVerifiedAttribute] =
                request.Level == Standing.IdentityVerified ? "true" : null,
        }, ct).ConfigureAwait(false);

        return await RecordAsync(
            context, $"compliance.account.level{request.Level}", userId, reason, ct).ConfigureAwait(false);
    }

    private async Task<AccountStandingDto> RecordAsync(
        HttpContext context, string action, string userId, string reason, CancellationToken ct)
    {
        var after = await GetAsync(userId, ct).ConfigureAwait(false);
        await _audit.RecordAsync(context, new AuditRecord(
            "compliance", action, "ok", Target: $"user:{userId}",
            Details: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["email"] = after.Email,
                ["reason"] = reason,
                ["level"] = after.Level.ToString(CultureInfo.InvariantCulture),
                ["frozen"] = after.Frozen ? "true" : "false",
            }), ct).ConfigureAwait(false);
        return after;
    }

    private async Task<KeycloakUser> LoadAsync(string userId, CancellationToken ct)
    {
        try
        {
            return await _admin.GetAsync(userId, ct).ConfigureAwait(false);
        }
        catch (IdentityException e) when (e.Code == IslaPay.Platform.Api.PlatformErrors.TokenInvalid)
        {
            // What the admin client says for "no such account", phrased for a
            // caller's own token. Here the account is somebody else's.
            throw NotFound();
        }
    }

    private static string RequireReason(string? reason)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        return trimmed.Length >= 4
            ? trimmed
            : throw new IdentityException(
                IdentityErrors.InvalidStandingChange, StatusCodes.Status422UnprocessableEntity,
                "a change to an account's standing says why; the person will ask.");
    }

    private static string Actor(ClaimsPrincipal user) =>
        user.FindFirstValue("preferred_username")
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue("sub")
        ?? "unknown";

    private static IdentityException NotFound() => new(
        IdentityErrors.AccountNotFound, StatusCodes.Status404NotFound, "there is no such account.");

    internal static AccountStandingDto Describe(KeycloakUser user)
    {
        var level = !user.PhoneVerified ? Standing.Unverified
            : user.IdentityVerified ? Standing.IdentityVerified : Standing.PhoneVerified;
        return new AccountStandingDto(
            user.Id, user.DisplayName, user.Email, user.Phone, user.PhoneVerified, user.IdentityVerified,
            level, Standing.MaxPerMovement(level).ToString(CultureInfo.InvariantCulture),
            user.Frozen, user.FrozenReason, user.FrozenAt, user.FrozenBy);
    }
}
