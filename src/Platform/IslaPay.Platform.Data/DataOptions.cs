namespace IslaPay.Platform.Data;

/// <summary>How to reach Postgres.</summary>
public sealed class DataOptions
{
    /// <summary>
    /// Npgsql connection string. Comes from the secret store in a deployment;
    /// the default is a local development cluster and nothing else.
    /// </summary>
    public string ConnectionString { get; init; } =
        "Host=localhost;Port=5432;Database=islapay;Username=postgres;Password=postgres";

    /// <summary>
    /// Whether the host applies pending migrations as it starts.
    /// </summary>
    /// <remarks>
    /// True in development and in tests, where the alternative is every
    /// developer running a script by hand and one of them forgetting. In
    /// production this is false and migrations are a deliberate step, because
    /// a rolling deploy runs the old and new code against one schema and the
    /// order of those two events has to be chosen rather than raced.
    /// </remarks>
    public bool MigrateOnStartup { get; init; }
}
