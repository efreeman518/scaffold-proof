using EF.Domain;
using EF.Domain.Contracts;
using DomainTagId = TaskFlow.Domain.Shared.TagId;
using DomainTenantId = TaskFlow.Domain.Shared.TenantId;

namespace TaskFlow.Domain.Model;

/// <summary>Models tag domain behavior and invariants.</summary>
public sealed class Tag : TaskFlowEntityBase<DomainTagId>, ITenantEntity<DomainTenantId>, IEquatable<Tag>
{
    public DomainTenantId TenantId { get; init; }
    public string Name { get; private set; } = null!;
    public string? Color { get; private set; }

    // Navigation
    public ICollection<TaskItemTag> TaskItemTags { get; private set; } = [];

    /// <summary>Initializes tag with required dependencies and default state.</summary>
    private Tag() { }

    /// <summary>Initializes tag with required dependencies and default state.</summary>
    private Tag(DomainTenantId tenantId, string name, string? color)
    {
        TenantId = tenantId;
        Name = name;
        Color = color;
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public static DomainResult<Tag> Create(DomainTenantId tenantId, string name, string? color = null)
    {
        var entity = new Tag(tenantId, name, color);
        return entity.Valid();
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    public DomainResult<Tag> Update(string? name = null, string? color = null)
    {
        if (name is not null) Name = name;
        if (color is not null) Color = color;
        return Valid();
    }

    /// <summary>Creates a valid tag instance with domain-required defaults.</summary>
    private DomainResult<Tag> Valid()
    {
        var errors = new List<DomainError>();
        if (TenantId.Value == Guid.Empty) errors.Add(DomainError.Create("Tenant ID cannot be empty."));
        if (string.IsNullOrWhiteSpace(Name)) errors.Add(DomainError.Create("Name is required."));
        return errors.Count > 0
            ? DomainResult<Tag>.Failure(errors)
            : DomainResult<Tag>.Success(this);
    }

    // Value-based equality: same TenantId + case-insensitive trimmed Name
    public bool Equals(Tag? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return TenantId == other.TenantId
            && string.Equals(Normalize(Name), Normalize(other.Name), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Provides the equals operation for tag.</summary>
    public override bool Equals(object? obj) => ReferenceEquals(this, obj) || obj is Tag t && Equals(t);

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public override int GetHashCode()
    {
        return HashCode.Combine(
            TenantId,
            (Normalize(Name) ?? string.Empty).ToUpperInvariant());
    }

    /// <summary>Normalizes input so callers and persistence use consistent values.</summary>
    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
