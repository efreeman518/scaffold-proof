using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TaskFlow.Api.Filters;

namespace TaskFlow.Api.OpenApi;

/// <summary>
/// Publishes the concurrency contract in the OpenAPI document so generated clients carry it. Without
/// this the If-Match header is invisible to code generation and every generated client would send
/// writes that the API answers with 428.
/// </summary>
internal sealed class ConcurrencyOperationTransformer : IOpenApiOperationTransformer
{
    private const string IfMatchHeader = "If-Match";
    private const string ETagHeader = "ETag";

    /// <summary>Adds the If-Match parameter and the 412/428 responses to routes that require the precondition.</summary>
    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var requiresIfMatch = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<IfMatchRequiredMetadata>()
            .Any();

        if (requiresIfMatch)
        {
            operation.Parameters ??= [];
            if (!operation.Parameters.Any(p => string.Equals(p.Name, IfMatchHeader, StringComparison.OrdinalIgnoreCase)))
            {
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = IfMatchHeader,
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Aggregate version from the resource's ETag, or * to overwrite unconditionally.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
            }

            AddResponse(operation, "412", "Precondition failed - the aggregate changed; the ETag header carries the current version.");
            AddResponse(operation, "428", "Precondition required - the If-Match header is missing.");
        }

        // Every success response that carries an entity envelope also carries its ETag.
        foreach (var response in operation.Responses ?? [])
        {
            if (!response.Key.StartsWith('2') || response.Value is not OpenApiResponse concrete) continue;

            concrete.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.OrdinalIgnoreCase);
            if (!concrete.Headers.ContainsKey(ETagHeader))
            {
                concrete.Headers[ETagHeader] = new OpenApiHeader
                {
                    Description = "Strong entity tag - the aggregate version to send back as If-Match.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                };
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Declares a failure response only when the document does not already have one.</summary>
    private static void AddResponse(OpenApiOperation operation, string statusCode, string description)
    {
        operation.Responses ??= [];
        if (!operation.Responses.ContainsKey(statusCode))
        {
            operation.Responses[statusCode] = new OpenApiResponse { Description = description };
        }
    }
}
