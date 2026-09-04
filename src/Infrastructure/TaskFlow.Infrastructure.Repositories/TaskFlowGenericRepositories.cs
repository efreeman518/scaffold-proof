using EF.Data;
using EF.Data.Contracts;
using EF.Domain.Contracts;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// Open-generic transactional repository bound to the TaskFlow write context. Registered as an open
/// generic (<c>typeof(IRepositoryTrxn&lt;,&gt;) -&gt; typeof(TaskFlowRepositoryTrxn&lt;,&gt;)</c>) so any
/// entity with no bespoke persistence logic resolves <c>IRepositoryTrxn&lt;TEntity, TId&gt;</c> without a
/// per-entity repository class. Bespoke repositories derive from it so every key lookup in the solution
/// goes through <see cref="GetAsync"/> below.
/// </summary>
public class TaskFlowRepositoryTrxn<TEntity, TId>(TaskFlowDbContextTrxn db)
    : RepositoryTrxn<TEntity, TId, TaskFlowDbContextTrxn>(db), IRepositoryTrxn<TEntity, TId>
    where TEntity : TaskFlowEntityBase<TId>, ITenantEntity<TenantId>
    where TId : struct, IDomainId<TId>
{
    // D-022: the primary key is (TenantId, Id); the package GetAsync uses FindAsync(id) with a single key
    // value, which no longer resolves. The package method is sealed, so the interface is re-implemented here
    // (every caller goes through IRepositoryTrxn). Filter on Id and let the tenant query filter supply TenantId.
    public new Task<TEntity?> GetAsync(TId id, CancellationToken cancellationToken = default) =>
        GetEntityAsync(true, filter: KeyFilter.ById<TEntity, TId>(id), cancellationToken: cancellationToken);
}

/// <summary>
/// Open-generic read repository bound to the TaskFlow no-tracking query context. Registered as an open
/// generic for entities with no bespoke read logic. Bespoke <c>I{Entity}RepositoryQuery</c> contracts
/// that need paged search extend <c>IRepositoryQuery&lt;TEntity, TId&gt;</c> and are registered explicitly.
/// </summary>
public class TaskFlowRepositoryQuery<TEntity, TId>(TaskFlowDbContextQuery db)
    : RepositoryQuery<TEntity, TId, TaskFlowDbContextQuery>(db), IRepositoryQuery<TEntity, TId>
    where TEntity : TaskFlowEntityBase<TId>, ITenantEntity<TenantId>
    where TId : struct, IDomainId<TId>
{
    // See TaskFlowRepositoryTrxn.GetAsync.
    public new Task<TEntity?> GetAsync(TId id, CancellationToken cancellationToken = default) =>
        GetEntityAsync(false, filter: KeyFilter.ById<TEntity, TId>(id), cancellationToken: cancellationToken);
}

internal static class KeyFilter
{
    /// <summary>Builds <c>e =&gt; e.Id == id</c> for a generic typed id (record structs have no static == inside generics).</summary>
    public static Expression<Func<TEntity, bool>> ById<TEntity, TId>(TId id)
        where TEntity : TaskFlowEntityBase<TId>
        where TId : struct, IDomainId<TId>
    {
        var entity = Expression.Parameter(typeof(TEntity), "e");
        // Field access on a boxed closure (not Expression.Constant) so EF emits a SQL parameter, not a literal.
        var box = new StrongBox<TId>(id);
        var body = Expression.Equal(
            Expression.Property(entity, nameof(TaskFlowEntityBase<TId>.Id)),
            Expression.Field(Expression.Constant(box), nameof(StrongBox<TId>.Value)));
        return Expression.Lambda<Func<TEntity, bool>>(body, entity);
    }
}
