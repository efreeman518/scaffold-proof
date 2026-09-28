using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TaskFlow.Infrastructure.Data.Conventions;

/// <summary>Defensive twin for any future DateTime property: written as UTC, read back with Kind = Utc.</summary>
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}
