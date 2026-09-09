using Pgvector;

namespace TaskFlow.Infrastructure.Data.ReadModel;

/// <summary>
/// Search projection holding one pgvector embedding per task (D-040). Like <see cref="TaskViewRecord"/> it is
/// a derivation of the write side, not domain state: no <c>ITenantEntity</c> filter (the tenant is passed
/// explicitly, background embedding work has no request context), no Version, no audit stamping.
/// <para>
/// Mapped only when the active provider is Npgsql - see <c>TaskFlowDbContextBase.OnModelCreating</c>. There is
/// deliberately no <c>DbSet</c> for it: a DbSet would put the type into the SQL Server model too through EF's
/// set convention, and the SQL Server migration snapshot must stay untouched.
/// </para>
/// </summary>
public sealed class TaskItemEmbedding
{
    /// <summary>
    /// Dimension the <c>vector</c> column is declared with, and therefore the one the deployed PostgreSQL
    /// migration created. It is a constant rather than a bound setting because changing it changes the
    /// column type: a configured value that disagreed with the migration would leave the EF model reporting
    /// pending changes and the HNSW index unusable. <c>Search:PgVector:Dimensions</c> is validated against
    /// this at startup, so a deployment that wants another embedding size is told to add a migration.
    /// </summary>
    public const int DefaultDimensions = 1536;

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Task this embedding was generated for.</summary>
    public Guid TaskItemId { get; set; }

    /// <summary>The embedding itself; cosine distance against it is the semantic ranking.</summary>
    public Vector Embedding { get; set; } = null!;

    /// <summary>Dimension count of the stored vector, recorded with the model so a re-embed sweep can find rows a newer model would resize.</summary>
    public int Dimensions { get; set; }

    /// <summary>Embedding model that produced the vector; vectors from different models are not comparable.</summary>
    public string ModelId { get; set; } = null!;

    /// <summary>When this row was last written.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }
}
