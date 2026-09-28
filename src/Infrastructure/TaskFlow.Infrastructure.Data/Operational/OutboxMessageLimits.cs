namespace TaskFlow.Infrastructure.Data.Operational;

/// <summary>Column limits of <see cref="OutboxMessage"/> shared by the mapping and the staging code that fills it.</summary>
public static class OutboxMessageLimits
{
    /// <summary>Length of a W3C <c>traceparent</c> (<c>00-{32 hex}-{16 hex}-{2 hex}</c>).</summary>
    public const int TraceParentLength = 55;

    /// <summary>W3C recommends propagating at least 512 characters of <c>tracestate</c>; longer values are dropped.</summary>
    public const int TraceStateLength = 512;
}
