using EF.CQRS.DependencyInjection;
using EF.CQRS.Validation;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Cqrs.Features.TaskItems;

namespace TaskFlow.Application.Cqrs.Registration;

/// <summary>Provides CQRS application registration behavior for the Application Registration layer.</summary>
public static class CqrsApplicationRegistration
{
    /// <summary>Registers task flow CQRS application dependencies in the service container.</summary>
    public static IServiceCollection AddTaskFlowCqrsApplication(this IServiceCollection services)
    {
        services.AddScoped<IRequestValidator<CreateTaskItemCommand>, CreateTaskItemCommandValidator>();
        services.AddScoped<IRequestValidator<UpdateTaskItemCommand>, UpdateTaskItemCommandValidator>();

        return services.AddDecoratedRequestHandlers(CqrsHandlerRegistrationCatalog.Registrations);
    }
}
