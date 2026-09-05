namespace TaskFlow.Uno.Core.Business.Models;

/// <summary>Carries tag data between Uno services and presentation models.</summary>
public record TagModel
{
    public Guid? Id { get; init; }
    public long? Version { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Color { get; init; }
}
