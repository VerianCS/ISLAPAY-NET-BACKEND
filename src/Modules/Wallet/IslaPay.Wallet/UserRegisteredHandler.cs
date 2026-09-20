using System.Text.Json;
using IslaPay.Identity.Contracts;
using IslaPay.Platform.Messaging;
using IslaPay.Platform.Serialization;
using Microsoft.Extensions.Logging;

namespace IslaPay.Wallet;

/// <summary>
/// Opens a new user's accounts when Identity says there is one.
/// </summary>
/// <remarks>
/// <para>
/// Idempotent, and it has to be twice over: the outbox delivers at least once,
/// and the same work happens again on the user's first wallet read. Opening an
/// account that is already open is a no-op, so both paths can run in any order
/// and any number of times.
/// </para>
/// <para>
/// This is the whole shape of integration between two contexts here. Wallet
/// does not call Identity, does not read its data and does not know it uses
/// Keycloak; it knows a routing key and a record. Replacing the in-process bus
/// with a network is a change of transport.
/// </para>
/// </remarks>
public sealed partial class UserRegisteredHandler : IMessageHandler
{
    private readonly WalletService _wallet;
    private readonly ILogger<UserRegisteredHandler> _log;

    public UserRegisteredHandler(WalletService wallet, ILogger<UserRegisteredHandler> log)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentNullException.ThrowIfNull(log);
        _wallet = wallet;
        _log = log;
    }

    public async Task HandleAsync(MessageEnvelope message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Bound to user.*.v1, so a key this build does not handle is normal
        // rather than an error. Acknowledged and ignored: dead-lettering
        // another team's new event would make adding one a breaking change.
        if (!string.Equals(message.RoutingKey, IdentityEvents.UserRegistered, StringComparison.Ordinal))
            return;

        var registered = JsonSerializer.Deserialize<UserRegistered>(
            message.Body.Span, IslaPayJson.Options);

        if (registered is null || string.IsNullOrWhiteSpace(registered.UserId))
        {
            // Malformed. Throwing dead-letters it, which is right: it will
            // never parse, and a human should see it.
            throw new InvalidOperationException(
                $"{IdentityEvents.UserRegistered} arrived without a usable userId.");
        }

        await _wallet.OpenAccountsAsync(registered.UserId, cancellationToken).ConfigureAwait(false);
        AccountsOpened(_log, registered.UserId);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Opened wallet accounts for {userId}")]
    private static partial void AccountsOpened(ILogger logger, string userId);
}
