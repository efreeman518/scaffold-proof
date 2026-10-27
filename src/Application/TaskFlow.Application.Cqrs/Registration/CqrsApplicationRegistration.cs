using EF.CQRS.DependencyInjection;
using EF.CQRS.Validation;
using EF.Domain.Contracts;
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

        // The validation decorator answers a failed validation with the response itself, so it needs an IFailable
        // response (every Result<T>). Searches answer a page, which cannot carry a failure, and have no validators:
        // they are registered without the decorator instead of with a factory that could only throw.
        foreach (var registration in CqrsHandlerRegistrationCatalog.Registrations)
        {
            var failable = IsFailable(registration.ResponseType);
            var validator = typeof(IRequestValidator<>).MakeGenericType(registration.RequestType);
            if (!failable && services.Any(d => d.ServiceType == validator))
                throw new InvalidOperationException(
                    $"{registration.RequestType.Name} has a validator but its response {registration.ResponseType.Name} cannot carry a validation failure.");

            services.AddDecoratedRequestHandler(
                registration.RequestType, registration.ResponseType, registration.HandlerType,
                configure: options => options.EnableValidation = failable);
        }

        return services;
    }

    private static bool IsFailable(Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFailable<>));
}
