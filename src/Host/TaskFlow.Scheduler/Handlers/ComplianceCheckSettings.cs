namespace TaskFlow.Scheduler.Handlers;

/// <summary>
/// How far ahead the daily compliance-check start looks: a tenant is started when it has an open task tagged
/// <c>compliance</c> due within <see cref="WindowDays"/> of the run, and the workflow is passed the same horizon as
/// <c>dueBefore</c>. Validated when the host starts.
/// </summary>
public sealed class ComplianceCheckSettings
{
    public const string ConfigSectionName = "Scheduling:Compliance";

    /// <summary>Upper bound on <see cref="WindowDays"/>: a year ahead is already a planning horizon, not a check.</summary>
    public const int MaxWindowDays = 365;

    /// <summary>Days after the run that a task may be due and still be checked; 1 to <see cref="MaxWindowDays"/>.</summary>
    public int WindowDays { get; set; } = 7;

    /// <summary>True when <see cref="WindowDays"/> is in range.</summary>
    public bool IsValid() => WindowDays is >= 1 and <= MaxWindowDays;
}
