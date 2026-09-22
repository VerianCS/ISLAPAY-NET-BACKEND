using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using IslaPay.Platform.Api;
using Microsoft.OpenApi;

namespace IslaPay.Platform.AspNet;

/// <summary>Whether this host publishes its own specification, and where.</summary>
/// <remarks>
/// Off outside Development unless somebody says otherwise. The routes are
/// discoverable by anyone holding a token, so this is not a secret — but a
/// complete map of the admin surface handed to unauthenticated callers is a
/// convenience for an attacker and for nobody else, and the people who need it
/// are generating a client at build time, from a machine that can be let in
/// deliberately.
/// </remarks>
public sealed class OpenApiOptions
{
    public bool? Enabled { get; init; }

    /// <summary>
    /// The route the document is served at. <c>/openapi/v1.json</c> by
    /// default, which is where every .NET generator looks first.
    /// </summary>
    public string Route { get; init; } = "/openapi/{documentName}.json";
}

/// <summary>
/// The API, described well enough for another repository to generate a client.
/// </summary>
/// <remarks>
/// <para>
/// Written down rather than hand-typed on the other side. The admin console is
/// a separate project and will never link against these assemblies, so without
/// a specification its types are somebody's transcription of what they saw in
/// a response — and a transcription drifts silently, which is exactly the
/// class of bug that has already cost this codebase a rate key and a field
/// that was absent rather than null.
/// </para>
/// <para>
/// Two things have to be taught, because reflection gets both wrong. Money is
/// a struct whose .NET shape is nothing like its wire shape, and the whole API
/// is bearer-authenticated through a scheme the generator cannot see from the
/// endpoints alone.
/// </para>
/// </remarks>
public static class OpenApiDocument
{
    public const string DocumentName = "v1";

    internal static void AddIslaPayOpenApi(IHostApplicationBuilder builder)
    {
        builder.Services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "IslaPay",
                    Version = "v1",
                    Description =
                        "Amounts are objects: {\"amount\": \"100.50\", \"currency\": \"EISLA\"}. "
                        + "The amount is a decimal string and never a JSON number, because a "
                        + "number is read as a float and loses exactness. Failures are "
                        + "application/problem+json and carry a 'code' — the only field a "
                        + "client may branch on.",
                };

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??=
                    new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
                document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description =
                        "An access token from POST /v1/auth/login. Admin routes additionally "
                        + "require a realm role: catalog-admin, treasury-admin or p2p-operator.",
                };

                return Task.CompletedTask;
            });

            // Every failure, once, instead of on every endpoint.
            //
            // The platform translates any module's refusal into the same
            // body, so listing the codes per operation would be 46 copies of
            // one fact, each free to go stale on its own. A generated client
            // needs the shape, not the enumeration: what it branches on is
            // `code`, and which codes an endpoint can raise is what
            // `STATUS.md` and the module's own contracts are for.
            options.AddOperationTransformer((operation, context, cancellationToken) =>
            {
                operation.Responses ??= new OpenApiResponses();
                operation.Responses["default"] = new OpenApiResponse
                {
                    Description =
                        "A refusal. application/problem+json, and 'code' is the only field "
                        + "a client may branch on.",
                    Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
                    {
                        [ProblemResults.ContentType] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchemaReference(nameof(ApiProblem)),
                        },
                    },
                };

                return Task.CompletedTask;
            });

            // Money, as it actually travels. Reflection sees MinorUnits and a
            // Currency struct; a client generated from that would send a shape
            // the converter refuses, and would refuse the shape it is sent.
            options.AddSchemaTransformer((schema, context, cancellationToken) =>
            {
                if (context.JsonTypeInfo.Type != typeof(Money)) return Task.CompletedTask;

                schema.Type = JsonSchemaType.Object;
                schema.Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                {
                    ["amount"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Description = "A decimal string in the currency's own scale: \"100.50\".",
                        Examples = [JsonValue.Create("100.50")],
                    },
                    ["currency"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Description = "A code from GET /v1/catalog/currencies.",
                        Examples = [JsonValue.Create("EISLA")],
                    },
                };
                schema.Required = new HashSet<string>(StringComparer.Ordinal)
                {
                    "amount",
                    "currency",
                };

                return Task.CompletedTask;
            });

            // ApiProblem is named by the response above and returned by no
            // endpoint's signature, so nothing would otherwise put it in the
            // document and every one of those references would dangle.
            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.AddComponent(nameof(ApiProblem), new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Description = "The body of every refusal this API makes.",
                    Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                    {
                        ["code"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Description =
                                "The machine-readable reason, and the only field to branch on.",
                            Examples = [JsonValue.Create("insufficient_funds")],
                        },
                        ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["status"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Integer,
                            Format = "int32",
                        },
                        ["detail"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String | JsonSchemaType.Null,
                            Description =
                                "The server's own words, for a log. Never shown to a user: it "
                                + "is neither translated nor written for one.",
                        },
                        ["type"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["instance"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String | JsonSchemaType.Null,
                        },
                        ["correlationId"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String | JsonSchemaType.Null,
                            Description =
                                "Ties a screenshot of this error to a line in the server's log.",
                        },
                        ["meta"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Object | JsonSchemaType.Null,
                            Description =
                                "Whatever the code needs to be actionable — the currency that "
                                + "was short, the field that was wrong.",
                        },
                    },
                    Required = new HashSet<string>(StringComparer.Ordinal)
                    {
                        "code",
                        "status",
                    },
                });

                return Task.CompletedTask;
            });
        });
    }

    internal static void MapIslaPayOpenApi(WebApplication app)
    {
        var options = app.Configuration.GetSection("OpenApi").Get<OpenApiOptions>()
            ?? new OpenApiOptions();

        if (options.Enabled ?? app.Environment.IsDevelopment())
        {
            app.MapOpenApi(options.Route);
        }
    }
}
