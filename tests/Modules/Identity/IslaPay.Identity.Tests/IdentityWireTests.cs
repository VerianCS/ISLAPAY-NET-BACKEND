using System.Text.Json;
using IslaPay.Identity.Contracts;
using IslaPay.Platform.Serialization;

namespace IslaPay.Identity.Tests;

/// <summary>
/// The identity shapes, locked the same way the wallet ones are.
/// </summary>
public class IdentityWireTests
{
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    [Fact]
    public void A_session_serialises_to_the_shape_the_client_reads()
    {
        var session = new AuthSessionResponse(
            new TokenPair("access.jwt.value", "refresh.jwt.value", 300, 1800),
            new UserDto(
                Id: "0f1e2d3c",
                Name: "Ana Pérez",
                Email: "ana@islapay.cu",
                Phone: "+5355123456",
                EmailVerified: false,
                PhoneVerified: false));

        var json = JsonSerializer.Serialize(session, Json);

        Assert.Contains("\"accessToken\":\"access.jwt.value\"", json, StringComparison.Ordinal);
        Assert.Contains("\"refreshToken\":\"refresh.jwt.value\"", json, StringComparison.Ordinal);
        // Seconds, not an instant: the client's clock is not to be trusted.
        Assert.Contains("\"expiresIn\":300", json, StringComparison.Ordinal);
        Assert.Contains("\"tokenType\":\"Bearer\"", json, StringComparison.Ordinal);
        Assert.Contains("\"phoneVerified\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_password_never_appears_in_a_response_type()
    {
        // Registration echoes the account back. If a password ever reached a
        // response DTO it would end up in a log, a crash report and a proxy
        // cache, so the absence is worth asserting rather than assuming.
        var user = new UserDto("id", "Ana", "ana@islapay.cu", "+5355123456", true, true);

        var json = JsonSerializer.Serialize(user, Json);

        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_null_phone_is_omitted_rather_than_written_as_null()
    {
        var user = new UserDto("id", "Ana", "ana@islapay.cu", null, false, false);

        var json = JsonSerializer.Serialize(user, Json);

        Assert.DoesNotContain("\"phone\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_deserialises_from_the_client_spelling()
    {
        const string Body = """
            {"email":"ana@islapay.cu","phone":"+5355123456","name":"Ana Pérez","password":"s3cret-enough"}
            """;

        var request = JsonSerializer.Deserialize<RegisterRequest>(Body, Json);

        Assert.NotNull(request);
        Assert.Equal("ana@islapay.cu", request!.Email);
        Assert.Equal("+5355123456", request.Phone);
    }

    [Fact]
    public void A_field_the_server_does_not_know_is_ignored_not_rejected()
    {
        // A client shipped before a field was retired keeps sending it. That
        // must not turn every one of its requests into a 400.
        const string Body = """
            {"email":"ana@islapay.cu","password":"s3cret-enough","deviceId":"legacy"}
            """;

        var request = JsonSerializer.Deserialize<LoginRequest>(Body, Json);

        Assert.NotNull(request);
        Assert.Equal("ana@islapay.cu", request!.Email);
    }
}
