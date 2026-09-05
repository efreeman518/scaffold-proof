namespace TaskFlow.Infrastructure.Caching.RateLimiting;

/// <summary>
/// Per-tenant rate-limit tiers. A tenant's allowance is a commercial decision, so it is configuration:
/// <c>RateLimiting:Tiers:{free|standard|premium}</c> defines the allowances and
/// <c>RateLimiting:TenantTiers:{tenantGuid}</c> assigns them. Unassigned tenants get <see cref="DefaultTier"/>.
/// </summary>
public class RateLimitingSettings
{
    public const string ConfigSectionName = "RateLimiting";

    public const string Free = "free";
    public const string Standard = "standard";
    public const string Premium = "premium";

    /// <summary>Redis connection string name. Without it the limiter stays in-process (single replica only).</summary>
    public string RedisConnectionStringName { get; set; } = "Redis1";

    /// <summary>Tier applied to a tenant with no explicit assignment.</summary>
    public string DefaultTier { get; set; } = Standard;

    /// <summary>Allowance per tier name.</summary>
    public Dictionary<string, RateLimitTier> Tiers { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [Free] = new() { PermitLimit = 20, WindowSeconds = 60 },
        [Standard] = new() { PermitLimit = 100, WindowSeconds = 60 },
        [Premium] = new() { PermitLimit = 1000, WindowSeconds = 60 }
    };

    /// <summary>Tenant id to tier name.</summary>
    public Dictionary<string, string> TenantTiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Allowance for the streaming export route, which holds a connection for as long as a tenant has rows.
    /// It gets its own budget so an export cannot consume a tenant's entire interactive allowance.
    /// </summary>
    public RateLimitTier Export { get; set; } = new() { PermitLimit = 5, WindowSeconds = 60 };

    /// <summary>Resolves the tier name for a tenant.</summary>
    public string TierFor(string? tenantId) =>
        tenantId is not null && TenantTiers.TryGetValue(tenantId, out var tier) ? tier : DefaultTier;

    /// <summary>Resolves the allowance for a tier name, falling back to the default tier then to standard.</summary>
    public RateLimitTier AllowanceFor(string tier) =>
        Tiers.TryGetValue(tier, out var allowance) ? allowance
        : Tiers.TryGetValue(DefaultTier, out var fallback) ? fallback
        : new RateLimitTier { PermitLimit = 100, WindowSeconds = 60 };
}

/// <summary>One tier's allowance.</summary>
public class RateLimitTier
{
    public int PermitLimit { get; set; } = 100;
    public int WindowSeconds { get; set; } = 60;
}
