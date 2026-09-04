using EF.Domain.Contracts;
using System.Linq.Expressions;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Mappers;

/// <summary>Maps attachment mapper domain objects and DTOs across application boundaries.</summary>
public static class AttachmentMapper
{
    public static readonly Expression<Func<Attachment, AttachmentDto>> Projection =
        entity => new AttachmentDto
        {
            Id = entity.Id.Value,
            Version = entity.Version,
            TenantId = entity.TenantId.Value,
            FileName = entity.FileName,
            ContentType = entity.ContentType,
            FileSizeBytes = entity.FileSizeBytes,
            StorageUri = entity.StorageUri,
            OwnerType = entity.OwnerType,
            OwnerId = entity.OwnerId
        };

    private static readonly Func<Attachment, AttachmentDto> Compiled = Projection.Compile();

    /// <summary>Converts the current value to DTO.</summary>
    public static AttachmentDto ToDto(this Attachment entity) => Compiled(entity);

    /// <summary>Converts the current value to entity.</summary>
    public static DomainResult<Attachment> ToEntity(this AttachmentDto dto, Guid tenantId)
        => Attachment.Create(DomainId.From<TenantId>(tenantId), dto.FileName, dto.ContentType, dto.FileSizeBytes, dto.StorageUri, dto.OwnerType, dto.OwnerId,
            DomainId.FromNullable<AttachmentId>(dto.Id));
}
