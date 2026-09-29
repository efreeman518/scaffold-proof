using EF.Data;
using EF.Data.Contracts;
using EF.Domain.Contracts;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// Open-generic transactional repository bound to the TaskFlow write context. Registered as an open
/// generic (<c>typeof(IRepositoryTrxn&lt;,&gt;) -&gt; typeof(TaskFlowRepositoryTrxn&lt;,&gt;)</c>) so any
/// entity with no bespoke persistence logic resolves <c>IRepositoryTrxn&lt;TEntity, TId&gt;</c> without a
/// per-entity repository class. The package <c>GetAsync</c> filters on a parameterized <c>Id</c> and the tenant
/// query filter supplies <c>TenantId</c>, so the tenant-first (TenantId, Id) key (D-022) resolves.
/// </summary>
public class TaskFlowRepositoryTrxn<TEntity, TId>(TaskFlowDbContextTrxn db)
    : RepositoryTrxn<TEntity, TId, TaskFlowDbContextTrxn>(db), IRepositoryTrxn<TEntity, TId>
    where TEntity : TaskFlowEntityBase<TId>, ITenantEntity<TenantId>
    where TId : struct, IDomainId<TId>;

/// <summary>
/// Open-generic read repository bound to the TaskFlow no-tracking query context. Registered as an open
/// generic for entities with no bespoke read logic. Bespoke <c>I{Entity}RepositoryQuery</c> contracts
/// that need paged search extend <c>IRepositoryQuery&lt;TEntity, TId&gt;</c> and are registered explicitly.
/// </summary>
public class TaskFlowRepositoryQuery<TEntity, TId>(TaskFlowDbContextQuery db)
    : RepositoryQuery<TEntity, TId, TaskFlowDbContextQuery>(db), IRepositoryQuery<TEntity, TId>
    where TEntity : TaskFlowEntityBase<TId>, ITenantEntity<TenantId>
    where TId : struct, IDomainId<TId>;
