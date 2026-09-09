using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Infrastructure.Data.ReadModel;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>
/// pgvector search projection (D-040). Deliberately has no parameterless constructor so
/// <c>ApplyConfigurationsFromAssembly</c> skips it - the same mechanism <see cref="TaskItemConfiguration"/>
/// relies on. It is applied explicitly, and only on the Npgsql provider, so the SQL Server model never
/// learns about the entity.
/// </summary>
/// <param name="dimensions">Vector column dimension; must match the deployed PostgreSQL migration.</param>
public sealed class TaskItemEmbeddingConfiguration(int dimensions) : IEntityTypeConfiguration<TaskItemEmbedding>
{
    public void Configure(EntityTypeBuilder<TaskItemEmbedding> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TaskItemEmbedding");
        // Tenant-first, like TaskView: one tenant's vectors are a contiguous key range.
        builder.HasKey(e => new { e.TenantId, e.TaskItemId });
        builder.Property(e => e.Embedding).HasColumnType($"vector({dimensions})").IsRequired();
        builder.Property(e => e.ModelId).HasMaxLength(128).IsRequired();

        // HNSW with the cosine operator class: the search orders by CosineDistance, and an index built for
        // another distance operator would simply not be used. Cosine is the right metric for the normalized
        // embeddings every supported model emits.
        builder.HasIndex(e => e.Embedding)
            .HasMethod("hnsw")
            .HasOperators("vector_cosine_ops")
            .HasDatabaseName("IX_TaskItemEmbedding_Embedding_Hnsw");
    }
}
