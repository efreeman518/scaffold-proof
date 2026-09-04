using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TaskFlow.Infrastructure.Data.Conventions;

/// <summary>
/// D-024: every DateTimeOffset is stored and compared as UTC on both providers. Npgsql's timestamptz rejects
/// non-zero offsets; SQL Server keeps the offset but then compares by instant anyway. EF applies the converter
/// to query parameters too, so a caller-supplied +05:00 filter value is normalized before it reaches the provider.
/// </summary>
public sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, DateTimeOffset>
{
    public UtcDateTimeOffsetConverter()
        : base(v => v.ToUniversalTime(), v => v.ToUniversalTime())
    {
    }
}

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
