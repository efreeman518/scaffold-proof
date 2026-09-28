using EF.BackgroundServices.Leased;

namespace TaskFlow.Scheduler.Workers;

/// <summary>
/// Poll, lease and batch shape of the outbox drain (D-026). The inherited defaults are the ones this drain ran
/// with before the loop moved into EF.BackgroundServices - a 1s floor, a 5s idle ceiling, a 5 minute lease and
/// 50 rows per claim - so the section exists to retune a deployment, not to make it work. The one value the
/// Scheduler's appsettings sets is <c>MaxAttempts: 10</c>, the ceiling this drain has always used; the inherited
/// default of 5 would halve the retry budget, and the claim and dead-letter policy both read this option.
/// </summary>
public class OutboxDispatcherSettings : LeasedWorkerOptions
{
    public const string ConfigSectionName = "OutboxDispatcher";
}
