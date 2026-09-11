using EF.BackgroundServices.Leased;

namespace TaskFlow.Scheduler.Workers;

/// <summary>
/// Poll, lease and batch shape of the outbox drain (D-026). The inherited defaults are the ones this drain ran
/// with before the loop moved into EF.BackgroundServices - a 1s floor, a 5s idle ceiling, a 5 minute lease and
/// 50 rows per claim - so the section exists to retune a deployment, not to make it work.
/// </summary>
public class OutboxDispatcherSettings : LeasedWorkerOptions
{
    public const string ConfigSectionName = "OutboxDispatcher";
}
