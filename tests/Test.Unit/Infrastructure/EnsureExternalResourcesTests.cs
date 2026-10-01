using EF.Common;
using EF.Storage.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskFlow.Bootstrapper.StartupTasks;
using TaskFlow.Infrastructure.Storage;

namespace Test.Unit.Infrastructure;

/// <summary>
/// D-052 provisioning lock serializes, it does not deduplicate: a replica that waited for the lock still runs
/// the idempotent ensure steps once it holds it, because a released lock only says the holder stopped - a
/// holder that threw (or died and let the TTL lapse) provisioned nothing.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class EnsureExternalResourcesTests
{
    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Waiter_AfterHolderReleasesWithoutProvisioning_StillProvisions()
    {
        var distributedLock = new InProcessDistributedLock();
        var provisioner = new CountingProvisioner();
        var sut = Build(distributedLock, provisioner);

        // The "holder" takes the lock and releases it without provisioning, as a holder that threw would.
        var holder = await distributedLock.TryAcquireAsync("taskflow:provision", TimeSpan.FromSeconds(60), TestContext.CancellationToken);
        Assert.IsNotNull(holder);
        var waiter = sut.ExecuteAsync(TestContext.CancellationToken);
        await holder.DisposeAsync();
        await waiter;

        Assert.AreEqual(1, provisioner.Calls, "the waiter must provision once it holds the lock");
    }

    [TestMethod]
    public async Task FirstReplica_HoldingTheLock_Provisions()
    {
        var provisioner = new CountingProvisioner();

        await Build(new InProcessDistributedLock(), provisioner).ExecuteAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, provisioner.Calls);
    }

    private static EnsureExternalResources Build(InProcessDistributedLock distributedLock, CountingProvisioner provisioner)
    {
        var services = new ServiceCollection()
            .AddSingleton<IS3BucketProvisioner>(provisioner)
            .BuildServiceProvider();

        return new EnsureExternalResources(
            services,
            new ConfigurationBuilder().Build(),
            new ProductionEnvironment(),
            Options.Create(new BlobStorageSettings()),
            distributedLock,
            NullLogger<EnsureExternalResources>.Instance);
    }

    private sealed class CountingProvisioner : IS3BucketProvisioner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task EnsureBucketExistsAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.CompletedTask;
        }

        public Task CheckBucketAsync(string bucketName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = nameof(Test.Unit);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
