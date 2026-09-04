using EF.Data;
using EF.Data.Contracts;
using EF.Domain.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using System.Linq.Expressions;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Repositories.Updaters;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// Transactional TaskItem repository. It returns tracked aggregates for command paths and loads
/// child collections when the application service needs to sync a full task graph.
/// </summary>
public class TaskItemRepositoryTrxn(TaskFlowDbContextTrxn db)
    : TaskFlowRepositoryTrxn<TaskItem, TaskItemId>(db), ITaskItemRepositoryTrxn
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<TaskItem?> GetTaskItemAsync(TaskItemId id, bool inclChildren = true, CancellationToken ct = default)
    {
        var includesList = new List<Expression<Func<IQueryable<TaskItem>, IIncludableQueryable<TaskItem, object?>>>>
        {
            q => q.Include(t => t.Category)
        };

        if (inclChildren)
        {
            includesList.Add(q => q.Include(t => t.Comments));
            includesList.Add(q => q.Include(t => t.ChecklistItems));
            includesList.Add(q => q.Include(t => t.TaskItemTags).ThenInclude(tt => tt.Tag));
            includesList.Add(q => q.Include(t => t.SubTasks));
        }

        return await GetEntityAsync(
            true,
            filter: t => t.Id == id,
            splitQueryThresholdOptions: SplitQueryThresholdOptions.Default,
            includes: [.. includesList],
            cancellationToken: ct
        ).ConfigureAwait(ConfigureAwaitOptions.None);
    }

    // Single-child tracked loads for the lean child-mutation path. Each filters on the owning
    // TaskItemId as well as the child id, so a child id belonging to another task returns null instead
    // of being mutated through the wrong root. These replace the five-include aggregate load that the
    // eight child endpoints previously paid for just to touch one row.

    /// <inheritdoc />
    public Task<Comment?> GetCommentAsync(TaskItemId taskItemId, CommentId commentId, CancellationToken ct = default) =>
        DB.Set<Comment>().FirstOrDefaultAsync(c => c.TaskItemId == taskItemId && c.Id == commentId, ct);

    /// <inheritdoc />
    public Task<ChecklistItem?> GetChecklistItemAsync(TaskItemId taskItemId, ChecklistItemId checklistItemId, CancellationToken ct = default) =>
        DB.Set<ChecklistItem>().FirstOrDefaultAsync(c => c.TaskItemId == taskItemId && c.Id == checklistItemId, ct);

    /// <inheritdoc />
    public Task<TaskItemTag?> GetTaskItemTagAsync(TaskItemId taskItemId, TagId tagId, CancellationToken ct = default) =>
        DB.Set<TaskItemTag>().FirstOrDefaultAsync(t => t.TaskItemId == taskItemId && t.TagId == tagId, ct);

    /// <summary>
    /// Delegates DTO graph sync to the DbContext updater so EF change tracking and related deletes
    /// happen inside the same unit of work.
    /// </summary>
    public DomainResult<TaskItem> UpdateFromDto(TaskItem entity, TaskItemDto dto, RelatedDeleteBehavior relatedDeleteBehavior = RelatedDeleteBehavior.None)
        => DB.UpdateFromDto(entity, dto, relatedDeleteBehavior);
}
