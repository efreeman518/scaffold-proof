using TaskFlow.Application.Models.Reads;
using TaskFlow.Contracts.Grpc;

namespace TaskFlow.Blazor.Services;

/// <summary>
/// D-054: DTO-returning wrappers over the generated gRPC client, so a page can choose its transport with
/// an ordinary conditional - both arms are the same <c>Task&lt;TDto&gt;</c> - instead of restructuring the
/// call around the generated <c>AsyncUnaryCall</c> shape.
///
/// They live in this host's namespace rather than in the contract assembly for a mundane reason: the
/// generated message types include <c>TaskItemStatus</c>, <c>Priority</c>, <c>Category</c> and <c>Tag</c>,
/// which collide with the domain enums and DTO names the Razor pages already import globally. Keeping the
/// helpers here means the pages never import the protobuf namespace.
/// </summary>
public static class TaskFlowReadClientExtensions
{
    /// <summary>Tenant task counts by status plus overdue and total.</summary>
    public static async Task<TaskItemSummaryDto> GetTaskItemSummaryDtoAsync(
        this TaskFlowRead.TaskFlowReadClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return (await client.GetTaskItemSummaryAsync(new GetTaskItemSummaryRequest(), cancellationToken: ct)).ToDto();
    }

    /// <summary>Full category and tag lists for pickers.</summary>
    public static async Task<TaskMetadataDto> GetTaskMetadataDtoAsync(
        this TaskFlowRead.TaskFlowReadClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return (await client.GetTaskMetadataAsync(new GetTaskMetadataRequest(), cancellationToken: ct)).ToDto();
    }
}
