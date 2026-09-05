using EF.Common.Contracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;

namespace TaskFlow.Functions;

/// <summary>Configures function category trigger host behavior for TaskFlow runtime services.</summary>
public class FunctionCategoryTrigger(
    ILogger<FunctionCategoryTrigger> logger,
    ICategoryService categoryService)
{
    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    [Function(nameof(CreateCategory))]
    public async Task<HttpResponseData> CreateCategory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/categories")] HttpRequestData req,
        CancellationToken ct)
    {
        var request = await req.ReadFromJsonAsync<CreateCategoryRequest>(cancellationToken: ct);
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { message = "Name is required." }, ct);
            return badRequest;
        }

        Result<DefaultResponse<CategoryDto>> result;
        try
        {
            result = await categoryService.CreateAsync(new DefaultRequest<CategoryDto>
            {
                Item = new CategoryDto
                {
                    Id = request.Id,
                    Name = request.Name.Trim(),
                    Description = request.Description,
                    SortOrder = request.SortOrder,
                    IsActive = request.IsActive
                }
            }, ct);
        }
        catch (IdempotentCreateConflictException)
        {
            // Caller-supplied id replayed with a different payload than the existing row (D-033).
            var conflict = req.CreateResponse(HttpStatusCode.Conflict);
            await conflict.WriteAsJsonAsync(new { message = "A category with this id already exists with different data." }, ct);
            return conflict;
        }

        if (result.IsFailure || result.Value?.Item == null)
        {
            logger.LogWarning("CreateCategory failed for request {Name}", request.Name);
            var failed = req.CreateResponse(HttpStatusCode.BadRequest);
            await failed.WriteAsJsonAsync(new { message = "Unable to create category." }, ct);
            return failed;
        }

        logger.CategoryCreated(result.Value.Item.Id);

        // A replay of a caller-supplied id is 200, not 201: nothing was created this time (D-033).
        var response = req.CreateResponse(result.Value.IsReplay ? HttpStatusCode.OK : HttpStatusCode.Created);
        if (result.Value.ETagVersion is long version)
        {
            response.Headers.Add("ETag", $"\"{version.ToString(CultureInfo.InvariantCulture)}\"");
        }
        await response.WriteAsJsonAsync(result.Value.Item, ct);
        return response;
    }

    /// <summary>Carries create category request CQRS data between endpoints and handlers.</summary>
    public sealed record CreateCategoryRequest
    {
        /// <summary>Optional caller-supplied UUIDv7 id; makes the create idempotent (D-033).</summary>
        public Guid? Id { get; init; }
        public string Name { get; init; } = null!;
        public string? Description { get; init; }
        public int SortOrder { get; init; }
        public bool IsActive { get; init; } = true;
    }
}
