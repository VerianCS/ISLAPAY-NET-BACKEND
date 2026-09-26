using IslaPay.Identity.Contracts;
using IslaPay.Platform.AspNet;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IslaPay.Identity;

/// <summary>
/// Everything the Identity context needs, and nothing the host has to know.
/// </summary>
/// <remarks>
/// The module owns its configuration section, its services and its routes.
/// The host's entire knowledge of Identity is that this type exists — which is
/// what makes "extract Identity into its own service" a move of this folder
/// rather than an archaeology of a shared startup file.
/// </remarks>
public sealed class IdentityModule : IIslaPayModule
{
    public string Name => "identity";

    public void AddServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var keycloak = builder.Configuration.GetSection("Keycloak").Get<KeycloakOptions>()
            ?? new KeycloakOptions();
        var otp = builder.Configuration.GetSection("Otp").Get<OtpOptions>()
            ?? new OtpOptions();

        builder.Services.AddSingleton(keycloak);
        builder.Services.AddSingleton(otp);

        builder.Services.AddHttpClient<ITokenClient, KeycloakTokenClient>(client =>
            client.Timeout = keycloak.HttpTimeout);

        // A singleton, not a typed client. A typed client is transient, and a
        // transient admin client re-authenticates on every call because the
        // token it caches dies with the instance.
        builder.Services.AddHttpClient(KeycloakAdminClient.HttpClientName, client =>
            client.Timeout = keycloak.HttpTimeout);
        builder.Services.AddSingleton<IAdminClient>(sp => new KeycloakAdminClient(
            sp.GetRequiredService<IHttpClientFactory>(), keycloak));

        builder.Services.AddSingleton<IOtpStore, InMemoryOtpStore>();

        // Realm roles and the second factor, read once per request for every
        // module, so no module has to know what a Keycloak token looks like.
        builder.Services.AddTransient<IClaimsTransformation, RealmRoleClaims>();

        // A sender that writes codes to the log is a development affordance
        // and nothing else.
        //
        // Outside Development the host refuses to start, here and now, rather
        // than registering something that throws when it is first used. The
        // difference matters: a deferred throw means the API comes up healthy,
        // serves traffic, and fails at the moment a real person is waiting for
        // a code — the worst possible time to discover that no SMS gateway was
        // ever wired in.
        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddSingleton<IOtpSender, DevelopmentOtpSender>();
        }
        else
        {
            throw new InvalidOperationException(
                "No SMS sender is configured. Register an IOtpSender implementation before "
                + $"running in {builder.Environment.EnvironmentName}; one-time codes cannot "
                + "be written to the log outside Development.");
        }

        // The read-only face other modules use. The admin client behind it
        // stays internal, because it can take over any account in the realm.
        builder.Services.AddSingleton<IUserDirectory, KeycloakUserDirectory>();

        builder.Services.AddSingleton<OtpService>();
        builder.Services.AddScoped<IdentityService>();

        // The platform asks; this module answers for its own dependency. The
        // health endpoint never learns the word "Keycloak".
        builder.Services.AddHttpClient<KeycloakReadiness>();
        builder.Services.AddSingleton<IReadinessCheck>(
            sp => sp.GetRequiredService<KeycloakReadiness>());
    }

    public void MapEndpoints(IEndpointRouteBuilder routes) => routes.MapAuth();
}

/// <summary>Whether the identity provider is answering.</summary>
internal sealed class KeycloakReadiness : IReadinessCheck
{
    private readonly HttpClient _http;
    private readonly KeycloakOptions _options;

    public KeycloakReadiness(HttpClient http, KeycloakOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        // Short: readiness is polled often and a slow answer is as good as no.
        _http.Timeout = TimeSpan.FromSeconds(3);
        _options = options;
    }

    public string Name => "identity-provider";

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http
                .GetAsync(new Uri(_options.MetadataAddress), cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
