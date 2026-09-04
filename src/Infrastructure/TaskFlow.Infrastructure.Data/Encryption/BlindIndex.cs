using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Security.Cryptography;
using System.Text;

namespace TaskFlow.Infrastructure.Data.Encryption;

// fallback: replace with EF.Data.Encryption.BlindIndex when published (package request 5).
/// <summary>
/// Deterministic equality token for a randomized-encrypted column: HMAC-SHA256 over the exact UTF-8 value
/// (no normalization), 32 bytes, keyed so the token reveals nothing without the key.
/// </summary>
public static class BlindIndex
{
    public const int SizeBytes = 32;
    public const string Annotation = "TaskFlow:BlindIndex";

    public static byte[] Compute(string value, byte[] key) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    // fallback: replace with EF.Data.Encryption PropertyBuilder.HasBlindIndex when published (package request 5).
    /// <summary>Declares the shadow property that <see cref="BlindIndexInterceptor"/> keeps in sync with this column.</summary>
    public static PropertyBuilder<string?> HasBlindIndex(this PropertyBuilder<string?> property, string shadowPropertyName) =>
        property.HasAnnotation(Annotation, shadowPropertyName);
}

// fallback: replace with EF.Data.Encryption.BlindIndexInterceptor when published (package request 5).
/// <summary>Populates every annotated blind-index shadow column on insert and whenever the source value changes.</summary>
public sealed class BlindIndexInterceptor(byte[] blindIndexKey) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Populate(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Populate(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Populate(DbContext? context)
    {
        if (context is null) return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            foreach (var property in entry.Metadata.GetProperties())
            {
                if (property.FindAnnotation(BlindIndex.Annotation)?.Value is not string shadowName) continue;

                var source = entry.Property(property.Name);
                if (entry.State == EntityState.Modified && !source.IsModified) continue;

                entry.Property(shadowName).CurrentValue = source.CurrentValue is string value
                    ? BlindIndex.Compute(value, blindIndexKey)
                    : null;
            }
        }
    }
}
