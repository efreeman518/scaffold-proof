namespace TaskFlow.Uno.Core.Business.Models;

/// <summary>Carries comment data between Uno services and presentation models.</summary>
public record CommentModel
{
    public Guid? Id { get; init; }
    public long? Version { get; init; }
    public string Body { get; init; } = string.Empty;
    public Guid TaskItemId { get; init; }
    public IReadOnlyList<AttachmentModel>? Attachments { get; init; }
}
