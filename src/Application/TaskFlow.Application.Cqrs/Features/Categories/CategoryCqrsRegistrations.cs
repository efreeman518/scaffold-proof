using EF.Common.Contracts;
using EF.CQRS.DependencyInjection;
using TaskFlow.Application.Models;
using EF.Domain.Contracts;

namespace TaskFlow.Application.Cqrs.Features.Categories;

/// <summary>Provides category CQRS registrations behavior for the Features Categories layer.</summary>
internal static class CategoryCqrsRegistrations
{
    public static IReadOnlyList<RequestHandlerRegistration> Registrations { get; } =
    [
        new(typeof(SearchCategoriesQuery), typeof(PagedResponse<CategoryDto>), typeof(SearchCategoriesHandler)),
        new(typeof(GetCategoryByIdQuery), typeof(Result<DefaultResponse<CategoryDto>>), typeof(GetCategoryByIdHandler)),
        new(typeof(CreateCategoryCommand), typeof(Result<DefaultResponse<CategoryDto>>), typeof(CreateCategoryHandler)),
        new(typeof(UpdateCategoryCommand), typeof(Result<DefaultResponse<CategoryDto>>), typeof(UpdateCategoryHandler)),
        new(typeof(DeleteCategoryCommand), typeof(Result<Unit>), typeof(DeleteCategoryHandler)),
    ];
}
