using IslaPay.Platform.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace IslaPay.Platform.AspNet;

/// <summary>
/// One bounded context, as the single process sees it.
/// </summary>
/// <remarks>
/// <para>
/// The unit of modularity. A module brings its own services and its own
/// routes; the host composes modules and decides nothing else about them. The
/// point is that adding, removing or extracting a context is a change to the
/// list in the host, not surgery on a shared startup file.
/// </para>
/// <para>
/// What a module must not do is reach into another module. Integration goes
/// through the other module's <c>.Contracts</c> assembly, and preferably
/// through an event rather than a call — that is the part which decides
/// whether this can ever become separate services. The architecture tests
/// enforce the reference rule; the event preference is a habit nothing can
/// enforce.
/// </para>
/// </remarks>
public interface IIslaPayModule
{
    /// <summary>Short name, used in logs and in the readiness report.</summary>
    string Name { get; }

    /// <summary>Registers everything the module needs, reading its own configuration section.</summary>
    void AddServices(IHostApplicationBuilder builder);

    /// <summary>Maps the module's routes. The host does not know what they are.</summary>
    void MapEndpoints(IEndpointRouteBuilder routes);
}

/// <summary>A dependency a module needs before it can serve traffic.</summary>
/// <remarks>
/// Readiness is composed rather than centralised: the platform cannot know
/// that Identity needs Keycloak, and hard-coding it in the health endpoint is
/// how a health check ends up lying about a module that was removed.
/// </remarks>
public interface IReadinessCheck
{
    /// <summary>What is being checked, as it appears in the response.</summary>
    string Name { get; }

    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}

/// <summary>Whether the database is answering.</summary>
/// <remarks>
/// Platform-level rather than per module: every module shares the cluster, so
/// one check reports it once instead of each module reporting the same outage
/// under a different name.
/// </remarks>
internal sealed class DatabaseReadiness : IReadinessCheck
{
    private readonly IDatabase _database;

    public DatabaseReadiness(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public string Name => "database";

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _database.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT 1;", connection);
            return (int?)await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) == 1;
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException)
        {
            return false;
        }
    }
}
