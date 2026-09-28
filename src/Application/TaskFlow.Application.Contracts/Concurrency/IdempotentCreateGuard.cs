using TaskFlow.Application.Models;

namespace TaskFlow.Application.Contracts.Concurrency;

/// <summary>
/// Replay detection for caller-supplied create ids (D-033). There is no idempotency table: the entity
/// row itself is the record, so a repeated create is "equivalent" when every scalar the caller can set
/// still matches.
///
/// Documented limitation: the compare deliberately ignores Id, Version, TenantId, and child
/// collections. Two creates that differ only in their children are treated as a replay, not a
/// conflict; children are mutated through their own sub-resource routes anyway.
/// </summary>
public static class IdempotentCreateGuard
{
    /// <summary>
    /// The D-033 outcome once a stored row already carries the caller's id: an equivalent payload is a replay
    /// of that row, a different one is a 409. Applies both to the pre-insert existence check and to a create
    /// whose insert lost the race to a concurrent create with the same id (both passed the existence check).
    /// </summary>
    public static DefaultResponse<TDto> ReplayOrThrow<TDto>(
        TDto existing, TDto incoming, Func<TDto, TDto, bool> isEquivalent, string entityType, Guid entityId)
    {
        ArgumentNullException.ThrowIfNull(isEquivalent);
        return isEquivalent(existing, incoming)
            ? new DefaultResponse<TDto> { Item = existing, IsReplay = true }
            : throw new IdempotentCreateConflictException(entityType, entityId);
    }

    /// <summary>True when a repeated TaskItem create carries the same scalar payload.</summary>
    public static bool IsEquivalent(TaskItemDto existing, TaskItemDto incoming) =>
        existing.Title == incoming.Title
        && existing.Description == incoming.Description
        && existing.Priority == incoming.Priority
        && existing.Features == incoming.Features
        && existing.EstimatedEffort == incoming.EstimatedEffort
        && existing.ActualEffort == incoming.ActualEffort
        && existing.CategoryId == incoming.CategoryId
        && existing.ParentTaskItemId == incoming.ParentTaskItemId
        && existing.StartDate == incoming.StartDate
        && existing.DueDate == incoming.DueDate
        && existing.RecurrenceInterval == incoming.RecurrenceInterval
        && existing.RecurrenceFrequency == incoming.RecurrenceFrequency
        && existing.RecurrenceEndDate == incoming.RecurrenceEndDate;

    /// <summary>True when a repeated Category create carries the same scalar payload.</summary>
    public static bool IsEquivalent(CategoryDto existing, CategoryDto incoming) =>
        existing.Name == incoming.Name
        && existing.Description == incoming.Description
        && existing.SortOrder == incoming.SortOrder
        && existing.IsActive == incoming.IsActive
        && existing.ParentCategoryId == incoming.ParentCategoryId;

    /// <summary>True when a repeated Tag create carries the same scalar payload.</summary>
    public static bool IsEquivalent(TagDto existing, TagDto incoming) =>
        existing.Name == incoming.Name && existing.Color == incoming.Color;

    /// <summary>True when a repeated Attachment create carries the same scalar payload.</summary>
    public static bool IsEquivalent(AttachmentDto existing, AttachmentDto incoming) =>
        existing.FileName == incoming.FileName
        && existing.ContentType == incoming.ContentType
        && existing.FileSizeBytes == incoming.FileSizeBytes
        && existing.OwnerType == incoming.OwnerType
        && existing.OwnerId == incoming.OwnerId;

    /// <summary>True when a repeated Comment create carries the same body.</summary>
    public static bool IsEquivalent(CommentDto existing, CommentDto incoming) =>
        existing.Body == incoming.Body;

    /// <summary>True when a repeated ChecklistItem create carries the same scalar payload.</summary>
    public static bool IsEquivalent(ChecklistItemDto existing, ChecklistItemDto incoming) =>
        existing.Title == incoming.Title
        && existing.IsCompleted == incoming.IsCompleted
        && existing.SortOrder == incoming.SortOrder;
}
