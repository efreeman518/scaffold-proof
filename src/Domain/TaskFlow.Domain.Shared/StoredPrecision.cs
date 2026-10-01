using TaskFlow.Domain.Shared.Constants;

namespace TaskFlow.Domain.Shared;

/// <summary>
/// Normalizes a value to the precision the database stores (D-033), so the domain value equals the persisted value
/// on both providers: effort is a <c>decimal(10, EFFORT_SCALE)</c> column, and a timestamp is stored to whole
/// microseconds (PostgreSQL) while .NET keeps 100 ns ticks. The create-replay compare uses the same helpers.
/// </summary>
public static class StoredPrecision
{
    private const long TicksPerMicrosecond = 10;

    /// <summary>Rounds effort to <see cref="DomainConstants.EFFORT_SCALE"/> decimals, midpoints away from zero.</summary>
    public static decimal? Effort(decimal? value) =>
        value.HasValue ? Math.Round(value.Value, DomainConstants.EFFORT_SCALE, MidpointRounding.AwayFromZero) : null;

    /// <summary>Truncates to whole microseconds, keeping the offset.</summary>
    public static DateTimeOffset? Timestamp(DateTimeOffset? value) =>
        value.HasValue ? Timestamp(value.Value) : null;

    /// <summary>Truncates to whole microseconds, keeping the offset.</summary>
    public static DateTimeOffset Timestamp(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TicksPerMicrosecond, value.Offset);
}
