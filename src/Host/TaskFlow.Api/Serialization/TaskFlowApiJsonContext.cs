using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace TaskFlow.Api.Serialization;

/// <summary>
/// D-048: host-side companion to <c>TaskFlowJsonContext</c> for the ASP.NET Core response shapes.
/// It lives here rather than in TaskFlow.Application.Models because <see cref="ProblemDetails"/> comes from
/// the ASP.NET Core shared framework, and Application.Models is a plain Microsoft.NET.Sdk library with no
/// FrameworkReference - adding one to register a host type would push the web framework down into the
/// application layer for a serialization detail.
///
/// ProblemDetails is worth generating: it is the response shape of every validation failure, 404, 409 and
/// unhandled exception, so on an error-heavy load it is as hot as the success DTOs. Options are inherited
/// from the owning HTTP JsonSerializerOptions, so the emitted JSON is unchanged.
/// </summary>
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
public partial class TaskFlowApiJsonContext : JsonSerializerContext;
