using EF.Domain;
using EF.Domain.Contracts;
using DomainCategoryId = TaskFlow.Domain.Shared.CategoryId;
using DomainTenantId = TaskFlow.Domain.Shared.TenantId;

namespace TaskFlow.Domain.Model;

/// <summary>Models category domain behavior and invariants.</summary>
public class Category : TaskFlowEntityBase<DomainCategoryId>, ITenantEntity<DomainTenantId>
{
    public DomainTenantId TenantId { get; init; }
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; }

    // Self-referencing hierarchy
    public DomainCategoryId? ParentCategoryId { get; private set; }
    public Category? ParentCategory { get; private set; }
    public ICollection<Category> SubCategories { get; private set; } = [];

    // Navigation
    public ICollection<TaskItem> TaskItems { get; private set; } = [];

    /// <summary>Initializes category with required dependencies and default state.</summary>
    private Category() { }

    /// <summary>Initializes category with required dependencies and default state.</summary>
    private Category(DomainTenantId tenantId, string name, string? description, int sortOrder, DomainCategoryId? parentCategoryId, DomainCategoryId? id)
    {
        if (id.HasValue) Id = id.Value; // D-033: caller-supplied UUIDv7 id makes create idempotent.
        TenantId = tenantId;
        Name = name;
        Description = description;
        SortOrder = sortOrder;
        IsActive = true;
        ParentCategoryId = parentCategoryId;
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public static DomainResult<Category> Create(DomainTenantId tenantId, string name, string? description = null, int sortOrder = 0, DomainCategoryId? parentCategoryId = null, DomainCategoryId? id = null)
    {
        var entity = new Category(tenantId, name, description, sortOrder, parentCategoryId, id);
        return entity.Valid();
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    public DomainResult<Category> Update(string? name = null, string? description = null, int? sortOrder = null, bool? isActive = null, DomainCategoryId? parentCategoryId = null)
    {
        if (name is not null) Name = name;
        if (description is not null) Description = description;
        if (sortOrder.HasValue) SortOrder = sortOrder.Value;
        if (isActive.HasValue) IsActive = isActive.Value;
        if (parentCategoryId.HasValue) ParentCategoryId = parentCategoryId.Value.Value == Guid.Empty ? null : parentCategoryId.Value;
        return Valid();
    }

    /// <summary>Creates a valid category instance with domain-required defaults.</summary>
    private DomainResult<Category> Valid()
    {
        var errors = new List<DomainError>();
        if (TenantId.Value == Guid.Empty) errors.Add(DomainError.Create("Tenant ID cannot be empty."));
        if (string.IsNullOrWhiteSpace(Name)) errors.Add(DomainError.Create("Name is required."));
        return errors.Count > 0
            ? DomainResult<Category>.Failure(errors)
            : DomainResult<Category>.Success(this);
    }
}
