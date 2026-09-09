namespace TaskFlow.Uno.Core.Business.Models;

/// <summary>Tenant-wide task counts computed by the server in one round trip (GET /task-items/summary).</summary>
public record TaskItemSummaryModel
{
    public IReadOnlyDictionary<string, int> ByStatus { get; init; } = new Dictionary<string, int>();
    public int Overdue { get; init; }
    public int Total { get; init; }
}
