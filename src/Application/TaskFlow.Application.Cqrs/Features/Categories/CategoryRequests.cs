using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using TaskFlow.Application.Models;

namespace TaskFlow.Application.Cqrs.Features.Categories;

/// <summary>Carries search categories query CQRS data between endpoints and handlers.</summary>
public sealed record SearchCategoriesQuery(SearchRequest<CategorySearchFilter> Request, bool IncludeTotal = false)
    : IQuery<PagedResponse<CategoryDto>>;

/// <summary>Carries get category by ID query CQRS data between endpoints and handlers.</summary>
public sealed record GetCategoryByIdQuery(Guid Id)
    : IQuery<Result<DefaultResponse<CategoryDto>>>;

/// <summary>Carries create category command CQRS data between endpoints and handlers.</summary>
public sealed record CreateCategoryCommand(DefaultRequest<CategoryDto> Request)
    : ICommand<Result<DefaultResponse<CategoryDto>>>;

/// <summary>Carries update category command CQRS data between endpoints and handlers.</summary>
public sealed record UpdateCategoryCommand(DefaultRequest<CategoryDto> Request, long? ExpectedVersion)
    : ICommand<Result<DefaultResponse<CategoryDto>>>;

/// <summary>Carries delete category command CQRS data between endpoints and handlers.</summary>
public sealed record DeleteCategoryCommand(Guid Id, long? ExpectedVersion)
    : ICommand<Result>;
