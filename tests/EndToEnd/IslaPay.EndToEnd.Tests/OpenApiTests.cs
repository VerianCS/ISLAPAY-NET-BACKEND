using System.Text.Json;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// The specification the host publishes about itself.
/// </summary>
/// <remarks>
/// <para>
/// There is a console being written in another repository, and it will never
/// link against these assemblies. Either it generates its client from this
/// document or somebody transcribes the shapes by reading a response — and a
/// transcription drifts silently, which is the class of bug that has already
/// cost this codebase a rate key, an enum value and a field that was absent
/// rather than null.
/// </para>
/// <para>
/// So the document is checked from outside, the same way the wire is. What
/// matters is not that a file exists but that the two things reflection gets
/// wrong are corrected: <c>Money</c>, whose .NET shape is nothing like the
/// object it travels as, and the bearer scheme, which no endpoint declares on
/// its own.
/// </para>
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class OpenApiTests
{
    private readonly IslaPayHostFixture _fixture;

    public OpenApiTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task The_host_describes_its_own_routes_without_a_token()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        using var client = host.CreateClient();

        // No Authorization header: whatever generates the client runs at build
        // time and has no account.
        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        // One route per module that has any, so a module dropped from the host
        // shows up here rather than in the console's next release.
        foreach (var route in new[]
        {
            "/v1/auth/login",
            "/v1/me/wallet",
            "/v1/listings",
            "/v1/catalog/currencies",
            "/v1/admin/treasury/balances",
            "/v1/admin/treasury/credits",
        })
        {
            Assert.True(paths.TryGetProperty(route, out _), $"{route} is not described.");
        }
    }

    [SkippableFact]
    public async Task Money_is_described_as_the_object_it_actually_travels_as()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        using var client = host.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync(
            new Uri("/openapi/v1.json", UriKind.Relative)));

        var money = document.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("Money");

        var properties = money.GetProperty("properties");

        // Reflection sees MinorUnits and a Currency struct. A client generated
        // from that would send a shape the converter refuses and refuse the
        // shape it is sent, and both failures would land on somebody who had
        // done nothing wrong.
        Assert.Equal("string", properties.GetProperty("amount").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("currency").GetProperty("type").GetString());
        Assert.False(properties.TryGetProperty("minorUnits", out _));

        var required = money.GetProperty("required").EnumerateArray()
            .Select(r => r.GetString()).ToList();
        Assert.Contains("amount", required);
        Assert.Contains("currency", required);
    }

    [SkippableFact]
    public async Task Every_response_is_a_named_type_and_every_failure_has_a_shape()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        using var client = host.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync(
            new Uri("/openapi/v1.json", UriKind.Relative)));

        // An endpoint written as `Results.Ok(x)` describes its 200 as "OK" and
        // nothing else, and a client generated from that gets `void` where the
        // body should be. `TypedResults.Ok(x)` is what puts the schema there,
        // and there is no way to notice the difference except from out here.
        var untyped = new List<string>();
        var anonymous = new List<string>();

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var responses = operation.Value.GetProperty("responses");

                // 204 has no body by definition; everything else that answers
                // 200 owes one a shape.
                if (responses.TryGetProperty("200", out var ok)
                    && !ok.TryGetProperty("content", out _))
                {
                    untyped.Add($"{operation.Name.ToUpperInvariant()} {path.Name}");
                }

                Assert.True(
                    responses.TryGetProperty("default", out _),
                    $"{operation.Name.ToUpperInvariant()} {path.Name} describes no failure.");
            }
        }

        // A projection written inline becomes
        // `AnonymousTypeOfstringAndstringAndint…`, which is a class name
        // nobody can use and a shape nobody can search for.
        foreach (var schema in document.RootElement
            .GetProperty("components").GetProperty("schemas").EnumerateObject())
        {
            if (schema.Name.StartsWith("AnonymousType", StringComparison.Ordinal))
                anonymous.Add(schema.Name);
        }

        Assert.True(untyped.Count == 0, $"Untyped 200s: {string.Join(", ", untyped)}");
        Assert.True(anonymous.Count == 0, $"Anonymous schemas: {string.Join(", ", anonymous)}");
    }

    [SkippableFact]
    public async Task The_bearer_scheme_is_declared_because_no_endpoint_declares_it()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        using var client = host.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync(
            new Uri("/openapi/v1.json", UriKind.Relative)));

        var bearer = document.RootElement
            .GetProperty("components").GetProperty("securitySchemes").GetProperty("bearer");

        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
    }
}
