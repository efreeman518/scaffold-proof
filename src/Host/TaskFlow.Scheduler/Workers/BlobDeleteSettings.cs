namespace TaskFlow.Scheduler.Workers;

/// <summary>
/// Bound on the deferred blob-delete drain (D-055). Configuration rather than a constant because the right
/// value is a property of the storage account and the replica count, not of this code: the useful ceiling is
/// what the account tolerates divided by the number of Scheduler replicas, and both change per environment.
/// </summary>
public class BlobDeleteSettings
{
    public const string ConfigSectionName = "BlobDelete";

    /// <summary>
    /// Deletes in flight per batch. 8 keeps a claimed batch of 50 to roughly 7 storage round trips instead of
    /// 50 while staying far below any account request limit; 1 restores the previous sequential behavior.
    /// </summary>
    public int MaxConcurrency { get; set; } = 8;
}
