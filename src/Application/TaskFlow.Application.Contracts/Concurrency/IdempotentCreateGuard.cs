using EF.Common.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared;

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
            : throw new ConflictException(entityType, entityId.ToString());
    }

    /// <summary>
    /// True when a repeated TaskItem create carries the same scalar payload, judged as the create applied it: the
    /// create attaches a recurrence pattern only when both the interval and the frequency are present
    /// (<c>TaskItemMapper.ToEntity</c>) and otherwise drops all three recurrence fields, so a resend of the same request
    /// compares those fields as dropped too.
    /// </summary>
    public static bool IsEquivalent(TaskItemDto existing, TaskItemDto incoming)
    {
        var recurs = incoming.RecurrenceInterval.HasValue && !string.IsNullOrEmpty(incoming.RecurrenceFrequency);
        return existing.Title == incoming.Title
            && existing.Description == incoming.Description
            && existing.Priority == incoming.Priority
            && existing.Features == incoming.Features
            && existing.EstimatedEffort == StoredPrecision.Effort(incoming.EstimatedEffort)
            && existing.ActualEffort == StoredPrecision.Effort(incoming.ActualEffort)
            && existing.CategoryId == incoming.CategoryId
            && existing.ParentTaskItemId == incoming.ParentTaskItemId
            && existing.StartDate == StoredPrecision.Timestamp(incoming.StartDate)
            && existing.DueDate == StoredPrecision.Timestamp(incoming.DueDate)
            && existing.RecurrenceInterval == (recurs ? incoming.RecurrenceInterval : null)
            && existing.RecurrenceFrequency == (recurs ? incoming.RecurrenceFrequency : null)
            && existing.RecurrenceEndDate == (recurs ? incoming.RecurrenceEndDate : null);
    }

    /// <summary>
    /// True when a repeated Category create carries the same scalar payload, judged as the create applied it: the
    /// create always stores <c>IsActive = true</c> (<c>Category.Create</c> takes no flag), so the request's
    /// <c>IsActive</c>, unset or not, is compared as true.
    /// </summary>
    public static bool IsEquivalent(CategoryDto existing, CategoryDto incoming) =>
        existing.Name == incoming.Name
        && existing.Description == incoming.Description
        && existing.SortOrder == incoming.SortOrder
        && existing.IsActive
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
