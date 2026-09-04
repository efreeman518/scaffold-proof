using EF.Domain.Contracts;

namespace TaskFlow.Domain.Shared;

public static class DomainId
{
    public static TId From<TId>(Guid value)
        where TId : struct, IDomainId<TId>
        => TId.From(value);

    public static TId? FromNullable<TId>(Guid? value)
        where TId : struct, IDomainId<TId>
        => value.HasValue ? TId.From(value.Value) : null;
}

public readonly record struct TenantId(Guid Value) : IDomainId<TenantId>
{
    public static TenantId From(Guid value) => new(value);
    public static implicit operator Guid(TenantId id) => id.Value;
    public override string ToString() => Value.ToString();
}

// Ordering operators exist only on TaskItemId: it is the keyset tiebreaker of the TaskItem cursor
// search, and EF needs a comparison node it can push into SQL. The database performs the comparison,
// so the provider's own uniqueidentifier collation orders both the WHERE and the ORDER BY - they
// agree with each other, which is all keyset paging requires.
public readonly record struct TaskItemId(Guid Value) : IDomainId<TaskItemId>, IComparable<TaskItemId>
{
    public static TaskItemId From(Guid value) => new(value);
    public static implicit operator Guid(TaskItemId id) => id.Value;
    public override string ToString() => Value.ToString();

    public int CompareTo(TaskItemId other) => Value.CompareTo(other.Value);
    public static bool operator <(TaskItemId left, TaskItemId right) => left.CompareTo(right) < 0;
    public static bool operator >(TaskItemId left, TaskItemId right) => left.CompareTo(right) > 0;
    public static bool operator <=(TaskItemId left, TaskItemId right) => left.CompareTo(right) <= 0;
    public static bool operator >=(TaskItemId left, TaskItemId right) => left.CompareTo(right) >= 0;
}

public readonly record struct CategoryId(Guid Value) : IDomainId<CategoryId>
{
    public static CategoryId From(Guid value) => new(value);
    public static implicit operator Guid(CategoryId id) => id.Value;
    public override string ToString() => Value.ToString();
}

public readonly record struct TagId(Guid Value) : IDomainId<TagId>
{
    public static TagId From(Guid value) => new(value);
    public static implicit operator Guid(TagId id) => id.Value;
    public override string ToString() => Value.ToString();
}

public readonly record struct CommentId(Guid Value) : IDomainId<CommentId>
{
    public static CommentId From(Guid value) => new(value);
    public static implicit operator Guid(CommentId id) => id.Value;
    public override string ToString() => Value.ToString();
}

public readonly record struct ChecklistItemId(Guid Value) : IDomainId<ChecklistItemId>
{
    public static ChecklistItemId From(Guid value) => new(value);
    public static implicit operator Guid(ChecklistItemId id) => id.Value;
    public override string ToString() => Value.ToString();
}

public readonly record struct AttachmentId(Guid Value) : IDomainId<AttachmentId>
{
    public static AttachmentId From(Guid value) => new(value);
    public static implicit operator Guid(AttachmentId id) => id.Value;
    public override string ToString() => Value.ToString();
}

public readonly record struct TaskItemTagId(Guid Value) : IDomainId<TaskItemTagId>
{
    public static TaskItemTagId From(Guid value) => new(value);
    public static implicit operator Guid(TaskItemTagId id) => id.Value;
    public override string ToString() => Value.ToString();
}
