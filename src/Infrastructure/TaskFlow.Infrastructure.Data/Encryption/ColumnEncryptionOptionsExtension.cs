using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

namespace TaskFlow.Infrastructure.Data.Encryption;

// fallback: replace with EF.Data.Encryption DbContextOptionsBuilder.UseColumnEncryption when published (package request 5).
/// <summary>
/// Carries the process encryptor into the context options so pooled contexts can resolve it in OnModelCreating
/// (<c>this.GetService&lt;IDbContextOptions&gt;().FindExtension&lt;ColumnEncryptionOptionsExtension&gt;()</c>). A different
/// encryptor instance yields a different internal service provider, and therefore a different cached model.
/// </summary>
public sealed class ColumnEncryptionOptionsExtension(IColumnEncryptor encryptor) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public IColumnEncryptor Encryptor { get; } = encryptor;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(ColumnEncryptionOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        private new ColumnEncryptionOptionsExtension Extension => (ColumnEncryptionOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => $"ColumnEncryption={Extension.Encryptor.GetType().Name} ";

        public override int GetServiceProviderHashCode() => RuntimeHelpers.GetHashCode(Extension.Encryptor);

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is ExtensionInfo info && ReferenceEquals(info.Extension.Encryptor, Extension.Encryptor);

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["TaskFlow:ColumnEncryption"] = Extension.Encryptor.GetType().Name;
    }
}

public static class ColumnEncryptionExtensions
{
    public static DbContextOptionsBuilder UseColumnEncryption(this DbContextOptionsBuilder options, IColumnEncryptor encryptor)
    {
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new ColumnEncryptionOptionsExtension(encryptor));
        return options;
    }

    public static DbContextOptionsBuilder<TContext> UseColumnEncryption<TContext>(
        this DbContextOptionsBuilder<TContext> options,
        IColumnEncryptor encryptor)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseColumnEncryption((DbContextOptionsBuilder)options, encryptor);

    /// <summary>Resolves the encryptor the model was built for; plaintext passthrough when none was registered (tests, design time).</summary>
    public static IColumnEncryptor GetColumnEncryptor(this DbContext context) =>
        context.GetService<IDbContextOptions>().FindExtension<ColumnEncryptionOptionsExtension>()?.Encryptor
        ?? PlaintextColumnEncryptor.Instance;

    // fallback: replace with EF.Data.Encryption services.AddColumnEncryption when published (package request 5).
    /// <summary>
    /// Registers <see cref="IColumnEncryptor"/>, <see cref="BlindIndexInterceptor"/> and the resolved
    /// <see cref="ColumnEncryptionKeys"/> as process singletons from <c>Database:Encryption</c>.
    /// </summary>
    public static IServiceCollection AddColumnEncryption(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ColumnEncryptionOptions>().Bind(configuration.GetSection(ColumnEncryptionOptions.SectionName));
        services.AddSingleton(sp =>
        {
            var keys = ColumnEncryptionKeys.Resolve(sp.GetRequiredService<IOptions<ColumnEncryptionOptions>>().Value);
            if (!keys.IsEnabled)
            {
                sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ColumnEncryptionExtensions))
                    .ColumnEncryptionDisabled(ColumnEncryptionOptions.SectionName);
            }
            return keys;
        });
        services.AddSingleton(sp => sp.GetRequiredService<ColumnEncryptionKeys>().CreateEncryptor());
        services.AddSingleton(sp => new BlindIndexInterceptor(sp.GetRequiredService<ColumnEncryptionKeys>().BlindIndexKey));
        return services;
    }
}
