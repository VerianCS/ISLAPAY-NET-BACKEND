namespace IslaPay.Platform.Api;

/// <summary>
/// An exception that already knows which contract error it is.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the platform's error middleware can translate a module's
/// failure without knowing the module. Before it, the pipeline caught
/// <c>IdentityException</c> by name, which meant the host could not host a
/// second module without being edited — the exact coupling a modular monolith
/// is supposed to avoid.
/// </para>
/// <para>
/// A module implements it on its own exception type and keeps its own base
/// class, so nothing about its internal error handling has to change.
/// </para>
/// </remarks>
public interface IApiFailure
{
    /// <summary>The stable code the client branches on.</summary>
    string Code { get; }

    /// <summary>The HTTP status this failure should produce.</summary>
    int Status { get; }

    /// <summary>Structured facts for the problem document's <c>meta</c>, or null.</summary>
    IReadOnlyDictionary<string, object>? Meta { get; }
}
