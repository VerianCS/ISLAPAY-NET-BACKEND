using System.Text.Json;
using IslaPay.Platform.Api;
using IslaPay.Platform.Data;
using IslaPay.Platform.AspNet.Security;
using IslaPay.Platform.Messaging;
using IslaPay.Platform.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace IslaPay.Platform.AspNet;

/// <summary>Where bearer tokens come from, as the pipeline sees it.</summary>
/// <remarks>
/// Deliberately says "OIDC issuer" and not "Keycloak". The platform validates
/// signatures against a discovery document; which product publishes it is the
/// Identity module's business, and the host is what connects the two. Keeping
/// the name generic is what stops the platform from acquiring a dependency on
/// a module.
/// </remarks>
public sealed class PlatformAuthOptions
{
    public required string Authority { get; init; }

    /// <summary>
    /// The audience a token must name. Not optional: an issuer normally serves
    /// several clients, and "signed by our issuer" is not "meant for us".
    /// </summary>
    public required string Audience { get; init; }

    /// <summary>
    /// Thirty seconds, not the five-minute default: an access token that lives
    /// ten minutes should not be usable for fifteen.
    /// </summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The pipeline every module gets, and the composition of modules into one host.
/// </summary>
public static class PlatformSetup
{
    /// <summary>
    /// Registers the shared pipeline, then lets each module register its own.
    /// </summary>
    /// <remarks>
    /// Modules are added in the order given and must not depend on that order.
    /// If one ever does, the dependency is real and belongs in an explicit
    /// contract rather than in a list's sequence.
    /// </remarks>
    public static WebApplicationBuilder AddIslaPayPlatform(
        this WebApplicationBuilder builder,
        PlatformAuthOptions auth,
        params IIslaPayModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(modules);

        builder.Services.AddSingleton(TimeProvider.System);

        // One database, one schema per module. The schemas are what make "one
        // database per service" a later move rather than a rewrite: nothing
        // reads across a schema boundary, so splitting the cluster is a
        // connection string change.
        var data = builder.Configuration.GetSection("Data").Get<DataOptions>() ?? new DataOptions();
        builder.Services.AddSingleton(data);
        builder.Services.AddSingleton<IDatabase, Database>();
        builder.Services.AddSingleton<IReadinessCheck, DatabaseReadiness>();
        builder.Services.AddSingleton<IdempotencyStore>();
        builder.Services.AddSingleton(new MigrationSet(
            "platform", typeof(IdempotencyStore).Assembly,
            "IslaPay.Platform.AspNet.Migrations."));

        // Messaging. Registered for every host because the outbox is how any
        // module publishes anything; a host with no producers and no
        // subscriptions simply drains an empty table.
        var messaging = builder.Configuration.GetSection("Messaging").Get<MessagingOptions>()
            ?? new MessagingOptions();
        var outbox = builder.Configuration.GetSection("Outbox").Get<OutboxOptions>()
            ?? new OutboxOptions();

        builder.Services.AddSingleton(messaging);
        builder.Services.AddSingleton(outbox);
        builder.Services.AddSingleton(new MigrationSet(
            "messaging", typeof(Outbox).Assembly, "IslaPay.Platform.Messaging.Migrations."));
        builder.Services.AddSingleton<RabbitMqBus>();
        builder.Services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<RabbitMqBus>());
        builder.Services.AddSingleton<IOutbox, Outbox>();
        builder.Services.AddSingleton<MessagingTopology>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MessagingTopology>());
        builder.Services.AddSingleton<OutboxPublisher>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxPublisher>());
        builder.Services.AddHostedService<RabbitMqSubscriber>();

        // The wire format comes from the platform, so a response from this
        // host and a response asserted in a module's contract test cannot be
        // serialised differently.
        //
        // Built from the container rather than from the static options,
        // because reading a Money needs to know what a currency code means and
        // only the catalogue knows that. Configure<T> resolves when the
        // options are first used, which is after start-up — so this does not
        // depend on the platform being wired before the module that registers
        // the catalogue, and it cannot be, since modules are added below.
        builder.Services.AddOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>()
            .Configure<ICurrencyScales>((options, scales) =>
            {
                var shared = IslaPayJson.Create(scales);
                options.SerializerOptions.PropertyNamingPolicy = shared.PropertyNamingPolicy;
                options.SerializerOptions.DefaultIgnoreCondition = shared.DefaultIgnoreCondition;
                options.SerializerOptions.UnmappedMemberHandling = shared.UnmappedMemberHandling;
                foreach (var converter in shared.Converters)
                    options.SerializerOptions.Converters.Add(converter);
            });

        AddBearerAuthentication(builder, auth);

        // The specification, so a client in another repository is generated
        // rather than transcribed. Registered before the modules because the
        // document is built from whatever routes they go on to map.
        OpenApiDocument.AddIslaPayOpenApi(builder);

        foreach (var module in modules)
        {
            builder.Services.AddSingleton(module);
            module.AddServices(builder);
        }

        return builder;
    }

    /// <summary>Wires the pipeline and maps every registered module's routes.</summary>
    public static async Task<WebApplication> UseIslaPayPlatformAsync(
        this WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var data = app.Services.GetRequiredService<DataOptions>();
        if (data.MigrateOnStartup)
        {
            // Before a single request is served: a module whose table does not
            // exist yet must fail here, on startup, rather than on the first
            // customer to touch it.
            await Migrator.ApplyAsync(
                app.Services.GetRequiredService<IDatabase>(),
                [.. app.Services.GetServices<MigrationSet>()],
                cancellationToken).ConfigureAwait(false);
        }

        // After the tables exist and before a single route is mapped. A module
        // that has to load something before it can answer — the currency
        // catalogue, which every other module reads synchronously — does it
        // here, where being too late is impossible.
        foreach (var task in app.Services.GetServices<IStartupTask>())
        {
            await task.RunAsync(cancellationToken).ConfigureAwait(false);
        }

        // Explicit, because the idempotency middleware needs two things that
        // only exist after these: the matched endpoint, to see whether it opted
        // in, and the caller, to scope the key to them.
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<IdempotencyMiddleware>();

        // Inside the idempotency middleware, not outside it.
        //
        // A refusal is usually raised as an exception, and if the translation
        // happened further out then idempotency would see an exception rather
        // than a 422 — and release the key, so the client's retry would run
        // the request again instead of being told the same thing twice. Here
        // the failure is already a response by the time idempotency looks at
        // it.
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception e) when (e is IApiFailure failure)
            {
                // Any module's failure, translated without the platform
                // knowing which module it came from.
                if (context.Response.HasStarted) throw;
                await ProblemResults.WriteAsync(context, failure, e.Message);
            }
            catch (Exception e) when (e is JsonException or BadHttpRequestException)
            {
                // A malformed body is the caller's problem, not a 500. Nothing
                // from the exception is echoed: it can quote the request.
                //
                // Both, because which one arrives depends on where the body is
                // read. A converter that throws inside an endpoint raises
                // `JsonException`; the same converter throwing while minimal
                // APIs bind the parameter is wrapped in
                // `BadHttpRequestException` before it ever reaches here. Only
                // catching the first left the second to the developer
                // exception page, which answers `text/plain` with a stack
                // trace — unparseable by the client, and a description of the
                // server's internals to anyone who sends a bad body.
                if (context.Response.HasStarted) throw;
                await ProblemResults.WriteAsync(
                    context, PlatformErrors.MalformedRequest, StatusCodes.Status400BadRequest,
                    "The request body could not be read as JSON.");
            }
        });

        MapHealth(app);
        PermissionAuthorization.MapStaffAccess(app);

        foreach (var module in app.Services.GetServices<IIslaPayModule>())
            module.MapEndpoints(app);

        // After the modules, because a document mapped before them would
        // describe an API with no routes in it.
        OpenApiDocument.MapIslaPayOpenApi(app);

        return app;
    }

    private static void AddBearerAuthentication(
        WebApplicationBuilder builder, PlatformAuthOptions auth)
    {
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = auth.Authority;
                options.MetadataAddress =
                    $"{auth.Authority.TrimEnd('/')}/.well-known/openid-configuration";
                options.Audience = auth.Audience;

                // Plain HTTP metadata is acceptable only where the issuer is
                // reached over a private network or a loopback — a development
                // machine or a test container. A switch, not a default.
                options.RequireHttpsMetadata = !auth.Authority.StartsWith(
                    "http://", StringComparison.OrdinalIgnoreCase);

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = auth.Authority,
                    ValidateAudience = true,
                    ValidAudience = auth.Audience,
                    ValidateLifetime = true,
                    // Keys come from the issuer's JWKS and rotate with it. A
                    // key pinned in configuration is a key that outlives its
                    // rotation and takes the API down with it.
                    ValidateIssuerSigningKey = true,
                    ClockSkew = auth.ClockSkew,
                };

                // The framework's default 401 has an empty body. The client
                // parses `code`, so every failure has to carry one.
                options.Events = new JwtBearerEvents
                {
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        await ProblemResults.WriteAsync(
                            context.HttpContext,
                            PlatformErrors.TokenInvalid,
                            StatusCodes.Status401Unauthorized,
                            context.ErrorDescription ?? "A valid bearer token is required.");
                    },
                    OnForbidden = async context =>
                    {
                        await PermissionAuthorization.RecordDenialAsync(context.HttpContext);
                        await ProblemResults.WriteAsync(
                            context.HttpContext,
                            PlatformErrors.Forbidden,
                            StatusCodes.Status403Forbidden,
                            "The token does not permit this.");
                    },
                };
            });

        builder.Services.AddAuthorization();

        // Routes ask for permissions, and the map from the roles in a token to
        // those permissions is the platform's, so it is one table.
        var security = builder.Configuration.GetSection("Security").Get<StaffSecurityOptions>()
            ?? new StaffSecurityOptions();
        builder.Services.AddPermissionPolicies(security);
    }

    /// <summary>
    /// Liveness and readiness, the second composed from what modules register.
    /// </summary>
    /// <remarks>
    /// They are separate because a restart fixes the first and never the
    /// second: an unreachable dependency is not something killing this process
    /// improves.
    /// </remarks>
    private static void MapHealth(WebApplication app)
    {
        app.MapGet("/health/live", () => TypedResults.Ok(new HealthReport("ok")))
            .AllowAnonymous();

        app.MapGet("/health/ready", async (
            IEnumerable<IReadinessCheck> checks, CancellationToken ct) =>
        {
            var results = new Dictionary<string, string>(StringComparer.Ordinal);
            var ready = true;

            foreach (var check in checks)
            {
                bool ok;
                try
                {
                    ok = await check.IsReadyAsync(ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A check that throws is a check that failed. Letting it
                    // escape would turn readiness into a 500, which reads as
                    // "the process is broken" rather than "a dependency is".
                    ok = false;
                }

                results[check.Name] = ok ? "ok" : "unreachable";
                ready &= ok;
            }

            return ready
                ? Results.Ok(new HealthReport("ready", results))
                : Results.Json(
                    new HealthReport("degraded", results),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        })
            .Produces<HealthReport>()
            .Produces<HealthReport>(StatusCodes.Status503ServiceUnavailable)
            .AllowAnonymous();
    }
}
