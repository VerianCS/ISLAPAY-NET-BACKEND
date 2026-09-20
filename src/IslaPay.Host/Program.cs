using IslaPay.Identity;
using IslaPay.Ledger;
using IslaPay.Platform.AspNet;

// The composition root, and nothing else.
//
// Everything this file does is name the modules and hand them a pipeline. It
// has no route, no service registration and no business decision of its own —
// which is the test of whether the modules are really modules. When a context
// is extracted into its own process, the change here is one line.

var builder = WebApplication.CreateBuilder(args);

// The one place that knows both halves of authentication: the issuer Identity
// signs tokens with, and the audience the pipeline demands. Deriving the
// platform's setting from the module's configuration keeps a single source of
// truth — two sections naming the same issuer drift, and the symptom is every
// token being rejected for a reason that points nowhere near the cause.
var keycloak = builder.Configuration.GetSection("Keycloak").Get<KeycloakOptions>()
    ?? new KeycloakOptions();

builder.AddIslaPayPlatform(
    new PlatformAuthOptions
    {
        Authority = keycloak.Issuer,
        Audience = keycloak.Audience,
    },
    new IdentityModule(),
    new LedgerModule());

var app = builder.Build();

await app.UseIslaPayPlatformAsync();

await app.RunAsync();

/// <summary>Exposed so the integration tests can host the real pipeline.</summary>
public partial class Program;
