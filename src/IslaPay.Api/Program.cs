using System.Text.Json;
using IslaPay.Api;
using IslaPay.Contracts;
using IslaPay.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- configuration

var keycloak = builder.Configuration.GetSection("Keycloak").Get<KeycloakOptions>()
    ?? new KeycloakOptions();
var otpOptions = builder.Configuration.GetSection("Otp").Get<OtpOptions>()
    ?? new OtpOptions();

builder.Services.AddSingleton(keycloak);
builder.Services.AddSingleton(otpOptions);
builder.Services.AddSingleton(TimeProvider.System);

// The wire format comes from the contracts package, so a response from this
// host and a response asserted in a contract test cannot be serialised
// differently.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    var shared = IslaPayJson.Options;
    options.SerializerOptions.PropertyNamingPolicy = shared.PropertyNamingPolicy;
    options.SerializerOptions.DefaultIgnoreCondition = shared.DefaultIgnoreCondition;
    options.SerializerOptions.UnmappedMemberHandling = shared.UnmappedMemberHandling;
    foreach (var converter in shared.Converters)
        options.SerializerOptions.Converters.Add(converter);
});

// ---------------------------------------------------------------- identity

builder.Services.AddHttpClient<ITokenClient, KeycloakTokenClient>(client =>
    client.Timeout = keycloak.HttpTimeout);

builder.Services.AddHttpClient<IAdminClient, KeycloakAdminClient>(client =>
    client.Timeout = keycloak.HttpTimeout);

builder.Services.AddSingleton<IOtpStore, InMemoryOtpStore>();

// A sender that writes codes to the log is a development affordance and
// nothing else.
//
// Outside Development the host refuses to start, here and now, rather than
// registering something that throws when it is first used. The difference
// matters: a deferred throw means the API comes up healthy, serves traffic,
// and fails at the moment a real person is waiting for a code — which is the
// worst possible time to discover that no SMS gateway was ever wired in.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IOtpSender, DevelopmentOtpSender>();
}
else
{
    throw new InvalidOperationException(
        "No SMS sender is configured. Register an IOtpSender implementation before "
        + $"running in {builder.Environment.EnvironmentName}; one-time codes cannot be "
        + "written to the log outside Development.");
}

builder.Services.AddSingleton<OtpService>();
builder.Services.AddScoped<IdentityService>();

// ---------------------------------------------------------------- authentication

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MetadataAddress = keycloak.MetadataAddress;
        // Keys are fetched from the realm's JWKS and rotate with it. A key
        // pinned in configuration is a key that outlives its rotation and
        // takes the API down with it.
        options.Authority = keycloak.Issuer;
        options.Audience = keycloak.Audience;

        // Plain HTTP metadata is acceptable only where Keycloak is reached
        // over a private network or a loopback — a development machine or a
        // test container. It is a configuration switch, not a default.
        options.RequireHttpsMetadata = !keycloak.Authority.StartsWith(
            "http://", StringComparison.OrdinalIgnoreCase);

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = keycloak.Issuer,
            ValidateAudience = true,
            ValidAudience = keycloak.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            // Thirty seconds, not the five-minute default: an access token
            // that lives ten minutes should not be usable for fifteen.
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        // The framework's default 401 has an empty body. The client parses
        // `code`, so every failure has to carry one — including this one.
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                await ProblemResults.WriteAsync(
                    context.HttpContext,
                    ErrorCodes.TokenInvalid,
                    StatusCodes.Status401Unauthorized,
                    context.ErrorDescription ?? "A valid bearer token is required.");
            },
            OnForbidden = context => ProblemResults.WriteAsync(
                context.HttpContext,
                ErrorCodes.Forbidden,
                StatusCodes.Status403Forbidden,
                "The token does not permit this."),
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// ---------------------------------------------------------------- pipeline

// First, so it also catches what the endpoints throw before any other
// middleware has written to the response.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (IdentityException failure)
    {
        if (context.Response.HasStarted) throw;
        await ProblemResults.WriteAsync(context, failure);
    }
    catch (JsonException)
    {
        // A malformed body is the caller's problem, not a 500. Nothing from
        // the exception is echoed: it can quote the request.
        if (context.Response.HasStarted) throw;
        await ProblemResults.WriteAsync(
            context, ErrorCodes.MalformedRequest, StatusCodes.Status400BadRequest,
            "The request body could not be read as JSON.");
    }
});

app.UseAuthentication();
app.UseAuthorization();

app.MapAuth();

// Liveness answers "is the process up", readiness "can it do its job". They
// are separate because a restart fixes the first and never the second: an
// unreachable Keycloak is not something killing this pod improves.
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.MapGet("/health/ready", async (KeycloakOptions options, IHttpClientFactory factory, CancellationToken ct) =>
{
    using var client = factory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(3);
    try
    {
        using var response = await client.GetAsync(new Uri(options.MetadataAddress), ct);
        return response.IsSuccessStatusCode
            ? Results.Ok(new { status = "ready" })
            : Results.Json(new { status = "degraded", identity = (int)response.StatusCode },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    {
        return Results.Json(new { status = "degraded", identity = "unreachable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();

await app.RunAsync();

/// <summary>Exposed so the integration tests can host the real pipeline.</summary>
public partial class Program;
