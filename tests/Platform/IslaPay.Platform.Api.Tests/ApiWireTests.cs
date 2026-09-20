using System.Text.Json;
using IslaPay.Platform.Api;
using IslaPay.Platform.Serialization;

namespace IslaPay.Platform.Api.Tests;

/// <summary>
/// The shapes every module's responses are wrapped in.
/// </summary>
/// <remarks>
/// Only what is module-independent lives here. The assertions about a
/// particular code — that <c>insufficient_funds</c> carries a currency, for
/// instance — belong to the module that owns that code, and are in that
/// module's tests. A platform test that reached into Wallet's catalogue would
/// be the shared kernel coming back through the test project.
/// </remarks>
public class ApiWireTests
{
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    [Fact]
    public void Optional_fields_are_omitted_rather_than_sent_as_null()
    {
        var json = JsonSerializer.Serialize(
            new ApiProblem(PlatformErrors.MalformedRequest, "Bad request", 400), Json);

        Assert.Equal(
            """{"code":"malformed_request","title":"Bad request","status":400}""",
            json);
    }

    [Fact]
    public void An_unknown_error_code_still_deserialises()
    {
        // A code this build has never heard of must not stop the client from
        // reading the response. It is why `code` is a string and not an enum.
        var problem = JsonSerializer.Deserialize<ApiProblem>(
            """{"code":"card_frozen","title":"Card frozen","status":409}""", Json);

        Assert.NotNull(problem);
        Assert.Equal("card_frozen", problem!.Code);
    }

    [Fact]
    public void Meta_survives_the_round_trip()
    {
        var problem = new ApiProblem(
            Code: "some_module_code",
            Title: "Something",
            Status: 422,
            Meta: new Dictionary<string, object> { ["currency"] = "USDT" });

        var json = JsonSerializer.Serialize(problem, Json);

        Assert.Contains("\"currency\":\"USDT\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_last_page_omits_the_cursor_entirely()
    {
        // null is the only reliable end-of-list signal, since a full page can
        // still be the final one.
        var json = JsonSerializer.Serialize(new CursorPage<string>(["a"]), Json);
        Assert.Equal("""{"items":["a"]}""", json);
    }
}
