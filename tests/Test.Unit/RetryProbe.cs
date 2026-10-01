using EF.Data.Contracts;
using Moq;

namespace Test.Unit;

/// <summary>
/// Mocks <see cref="IRepositoryBase.RetryOnConcurrencyAsync"/> to run its work once, as when no race is lost, and
/// records whether each save ran inside it. Pure-unit tier: the race itself is proven on the relational providers by
/// <c>Test.Integration</c>; this probe pins which writes go through the retry (D-073).
/// </summary>
internal sealed class RetryProbe
{
    private bool _inside;

    /// <summary>Times the work ran inside the mocked retry.</summary>
    public int Retries { get; private set; }

    /// <summary>Saves made from inside the retry's work.</summary>
    public int SavesInside { get; private set; }

    /// <summary>Saves made with no retry around them.</summary>
    public int SavesOutside { get; private set; }

    /// <summary>Attaches the probe: the retry runs its work once and every <c>Throw</c> save is counted.</summary>
    public RetryProbe Attach<TRepo>(Mock<TRepo> repo) where TRepo : class, IRepositoryBase
    {
        repo.Setup(r => r.RetryOnConcurrencyAsync(
                It.IsAny<Func<CancellationToken, Task<It.IsAnyType>>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new InvocationFunc(RunInside));
        repo.Setup(r => r.SaveChangesAsync(It.IsAny<OptimisticConcurrencyWinner>(), It.IsAny<CancellationToken>()))
            .Callback(RecordSave)
            .ReturnsAsync(1);
        return this;
    }

    /// <summary>Counts one save, for a repository write whose save the mock does not route through SaveChanges.</summary>
    public void RecordSave()
    {
        if (_inside) SavesInside++;
        else SavesOutside++;
    }

    /// <summary>Makes a mocked retry run its work once, for tests that do not inspect the probe.</summary>
    public static void PassThrough<TRepo>(Mock<TRepo> repo) where TRepo : class, IRepositoryBase =>
        repo.Setup(r => r.RetryOnConcurrencyAsync(
                It.IsAny<Func<CancellationToken, Task<It.IsAnyType>>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new InvocationFunc(invocation => Invoke(invocation)));

    private object RunInside(IInvocation invocation)
    {
        Retries++;
        _inside = true;
        try
        {
            var task = Invoke(invocation);
            // Every awaited mock completes synchronously, so the work has finished when the call returns.
            Assert.IsTrue(task.IsCompleted, "the probed work must complete synchronously");
            return task;
        }
        finally
        {
            _inside = false;
        }
    }

    private static Task Invoke(IInvocation invocation) =>
        (Task)((Delegate)invocation.Arguments[0]).DynamicInvoke(invocation.Arguments[2])!;
}
