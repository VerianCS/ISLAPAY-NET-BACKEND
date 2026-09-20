using System.Collections.Concurrent;
using IslaPay.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IslaPay.Identity.Tests;

/// <summary>
/// Captures the codes instead of sending them.
/// </summary>
/// <remarks>
/// The only thing replaced in the whole pipeline. Everything else — the bearer
/// middleware, the problem-details writer, the Keycloak clients — is the code
/// that ships, talking to a Keycloak that is really running. A test that
/// stubbed the identity provider would pass against a realm that does not
/// exist.
/// </remarks>
public sealed class RecordingOtpSender : IOtpSender
{
    private readonly ConcurrentDictionary<(OtpPurpose, string), string> _sent = new();

    public Task SendAsync(
        OtpPurpose purpose, string destination, string code, CancellationToken ct = default)
    {
        _sent[(purpose, destination)] = code;
        return Task.CompletedTask;
    }

    public string CodeFor(OtpPurpose purpose, string destination) =>
        _sent.TryGetValue((purpose, destination), out var code)
            ? code
            : throw new InvalidOperationException(
                $"No {purpose} code was sent to {destination}.");

    public bool AnythingSentTo(OtpPurpose purpose, string destination) =>
        _sent.ContainsKey((purpose, destination));
}

/// <summary>The real host, pointed at the fixture's realm and database.</summary>
public sealed class IslaPayApiFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string> _settings;

    public IslaPayApiFactory(IReadOnlyDictionary<string, string> settings) => _settings = settings;

    public RecordingOtpSender Codes { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);

        // Warning, not the Development default of Debug.
        //
        // The host logs every request, every HTTP call to Keycloak and every
        // hosted-service transition. Across five suites that is half a million
        // characters of CI log, and the one thing it reliably buries is the
        // test failure you are looking for — which cost an afternoon of
        // guessing at a red build whose reason was in there somewhere.
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Warning");

        // Through configuration, not through DI.
        //
        // The bearer middleware reads its realm, issuer and audience while the
        // host is being built, from the configuration, and keeps them. Swapping
        // the options object in the container afterwards changes what the
        // Keycloak clients use and leaves the middleware pointed at whatever
        // appsettings.json said — so sign-in would work and every token would
        // then be rejected. That failure looked like a Keycloak problem for
        // twenty minutes; this comment is cheaper than the second twenty.
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);

        // Codes expire in seconds here so the expiry path is a test and not a
        // five-minute wait, and the resend cooldown is off so a test can ask
        // twice.
        builder.UseSetting("Otp:Lifetime", "00:00:30");
        builder.UseSetting("Otp:ResendCooldown", "00:00:00");
        builder.UseSetting("Otp:MaxAttempts", "3");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOtpSender>();
            services.AddSingleton<IOtpSender>(Codes);
        });
    }
}

internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Removes every registration of a service.
    /// </summary>
    /// <remarks>
    /// Not <c>Replace</c>: that swaps the last registration and leaves the
    /// others, so a resolve of <c>IEnumerable&lt;T&gt;</c> would still see the
    /// production one. Here that would mean a real SMS attempt during a test.
    /// </remarks>
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
            services.Remove(descriptor);
    }
}
