using System.Text.Json.Serialization;

namespace IslaPay.Platform.Api;

/// <summary>
/// RFC 9457 Problem Details, plus the fields IslaPay adds.
/// </summary>
/// <param name="Code">
/// Stable, machine-readable. This is what the client switches on — never
/// <paramref name="Detail"/>, which is prose and free to be reworded or
/// translated. See <see cref="PlatformErrors"/> and each module’s own codes.
/// </param>
/// <param name="Title">Short, human-readable summary of the problem type.</param>
/// <param name="Status">The HTTP status code.</param>
/// <param name="Detail">
/// Explanation of this specific occurrence. Safe to show to a developer; not
/// intended as user-facing copy, which the client writes itself.
/// </param>
/// <param name="Type">URI identifying the problem type.</param>
/// <param name="Instance">The path that produced it.</param>
/// <param name="CorrelationId">
/// Ties the response to the server-side logs for this request. Worth
/// surfacing in the client's error UI: it is what makes a user's report
/// actionable.
/// </param>
/// <param name="Meta">
/// Structured facts about this occurrence. Some codes require particular keys
/// — each module publishes its own <c>RequireCurrencyMeta</c> set.
/// </param>
public sealed record ApiProblem(
    [property: JsonPropertyOrder(1)] string Code,
    [property: JsonPropertyOrder(2)] string Title,
    [property: JsonPropertyOrder(3)] int Status,
    [property: JsonPropertyOrder(4)] string? Detail = null,
    [property: JsonPropertyOrder(5)] string? Type = null,
    [property: JsonPropertyOrder(6)] string? Instance = null,
    [property: JsonPropertyOrder(7)] string? CorrelationId = null,
    [property: JsonPropertyOrder(8)] IReadOnlyDictionary<string, object>? Meta = null);
