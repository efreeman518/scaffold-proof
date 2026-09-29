using EF.Data;
using EF.Data.Contracts;
using EF.Domain;
using EF.Domain.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Infrastructure.Repositories.Updaters;

/// <summary>Provides task item updater behavior for the Infrastructure Updaters layer.</summary>
internal static class TaskItemUpdater
{
    /// <summary>
    /// Applies the parent TaskItem DTO and synchronizes comments, checklist items, and tag links
    /// into the loaded aggregate graph. Create-mode UIs can send child records with client-only ids
    /// or empty TaskItemId values; the updater creates real children under the parent aggregate.
    /// When RelatedDeleteBehavior is RelationshipAndEntity, missing children are hard-deleted so
    /// a single parent save can represent client-side removals.
    /// </summary>
    public static DomainResult<TaskItem> UpdateFromDto(this TaskFlowDbContextTrxn db, TaskItem entity, TaskItemDto dto,
        RelatedDeleteBehavior relatedDeleteBehavior = RelatedDeleteBehavior.None)
    {
        return entity.Update(
            title: dto.Title,
            description: dto.Description,
            priority: dto.Priority,
            features: dto.Features,
            estimatedEffort: dto.EstimatedEffort,
            actualEffort: dto.ActualEffort,
            categoryId: DomainId.FromNullable<CategoryId>(dto.CategoryId),
            parentTaskItemId: DomainId.FromNullable<TaskItemId>(dto.ParentTaskItemId))
        .Bind(updatedEntity => DomainResult.Combine(
            // Children are mutated through the aggregate root's own methods (AddComment /
            // RemoveComment etc.), never by touching the navigation collections directly.
            // New children added to the loaded, tracked parent are inferred as Added on save
            // because EntityBase.Id is configured ValueGeneratedNever (EntityBaseConfiguration) -
            // EF treats the client-set Guid v7 key as application-assigned, not store-generated, so
            // a navigation-add is not mistaken for an UPDATE. removeFunc still calls db.Delete() so
            // EF detaches the orphaned row from the change tracker.
            CollectionUtility.SyncCollectionWithResult<Comment, CommentDto, Guid>(
                updatedEntity.Comments,
                dto.Comments ?? [],
                e => e.Id.Value,
                i => i.Id,
                incomingDto => updatedEntity.AddComment(incomingDto.Body),
                (existing, incomingDto) => existing.Update(incomingDto.Body),
                toRemove =>
                {
                    if (relatedDeleteBehavior == RelatedDeleteBehavior.None) return DomainResult.Success();
                    updatedEntity.RemoveComment(toRemove);
                    db.Delete(toRemove);
                    return DomainResult.Success();
                }
            ),
            CollectionUtility.SyncCollectionWithResult<ChecklistItem, ChecklistItemDto, Guid>(
                updatedEntity.ChecklistItems,
                dto.ChecklistItems ?? [],
                e => e.Id.Value,
                i => i.Id,
                incomingDto =>
                {
                    var result = updatedEntity.AddChecklistItem(incomingDto.Title, incomingDto.SortOrder);
                    // Apply IsCompleted immediately - AddChecklistItem/Create don't take it,
                    // so a buffered "checked" item from the client would lose that state
                    // without this follow-up Update on the newly created child.
                    if (result.IsSuccess && incomingDto.IsCompleted)
                    {
                        result.Value!.Update(isCompleted: true);
                    }
                    return result;
                },
                (existing, incomingDto) => existing.Update(incomingDto.Title, incomingDto.IsCompleted, incomingDto.SortOrder),
                toRemove =>
                {
                    if (relatedDeleteBehavior == RelatedDeleteBehavior.None) return DomainResult.Success();
                    updatedEntity.RemoveChecklistItem(toRemove);
                    db.Delete(toRemove);
                    return DomainResult.Success();
                }
            ),
            CollectionUtility.SyncCollectionWithResult<TaskItemTag, TagDto, Guid>(
                updatedEntity.TaskItemTags,
                dto.Tags ?? [],
                e => e.TagId.Value,
                i => i.Id,
                incomingDto => updatedEntity.AssociateTag(TagId.From(incomingDto.Id!.Value)),
                removeFunc: toRemove =>
                {
                    if (relatedDeleteBehavior == RelatedDeleteBehavior.None) return DomainResult.Success();
                    updatedEntity.RemoveTag(toRemove);
                    db.Delete(toRemove);
                    return DomainResult.Success();
                }
            ))
            .Map(updatedEntity)
        );
    }
}
