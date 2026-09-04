using EF.BackgroundServices.InternalMessageBus;
using EF.Common.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskFlow.Application.Contracts.Paging;
using TaskFlow.Bootstrapper.Paging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Cqrs.Registration;
using TaskFlow.Application.MessageHandlers;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Application.Services;
using TaskFlow.Observability.Meters;

namespace TaskFlow.Bootstrapper;

/// <summary>Configures register services host behavior for TaskFlow runtime services.</summary>
public static partial class RegisterServices
{
    /// <summary>Registers application services dependencies in the service container.</summary>
    private static void AddApplicationServices(IServiceCollection services, IConfiguration config)
    {
        AddMessageHandlers(services);
        AddSharedApplicationServices(services);
        AddServiceApplicationServices(services);

        if (ApplicationStyleResolver.Resolve(config[ApplicationStyleResolver.ConfigKey]) == ApplicationStyle.Cqrs)
        {
            services.AddTaskFlowCqrsApplication();
        }

        services.AddScoped<ITaskViewProjectionService, TaskViewProjectionService>();
        services.TryAddSingleton<MessagingMetrics>();
    }

    /// <summary>Registers shared application services dependencies in the service container.</summary>
    private static void AddSharedApplicationServices(IServiceCollection services)
    {
        services.AddScoped<ITenantBoundaryValidator, TenantBoundaryValidator>();
        services.AddSingleton<IEntityCacheProvider, NoOpEntityCacheProvider>();

        // Documented exception to the Service/CQRS split: the aggregate read model (summary, metadata,
        // export) is a pure projection with no domain behavior to duplicate, so both styles share it.
        services.AddScoped<ITaskFlowReadService, TaskFlowReadService>();

        // Idempotent - the web host may already have configured a persisted key ring (Program.cs).
        services.AddDataProtection();
        services.AddSingleton<ICursorProtector, DataProtectionCursorProtector>();
    }

    /// <summary>Registers service application services dependencies in the service container.</summary>
    private static void AddServiceApplicationServices(IServiceCollection services)
    {
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<ITagService, TagService>();
        services.AddScoped<ITaskItemService, TaskItemService>();
        services.AddScoped<ICommentService, CommentService>();
        services.AddScoped<IChecklistItemService, ChecklistItemService>();
        services.AddScoped<IAttachmentService, AttachmentService>();
    }

    /// <summary>Registers message handlers dependencies in the service container.</summary>
    private static void AddMessageHandlers(IServiceCollection services)
    {
        services.AddScoped<IMessageHandler<AuditEntry<string, Guid>>, AuditHandler>();
        services.AddScoped<IMessageHandler<AuditEntry<string, Guid?>>, AuditHandler>();
        services.AddScoped<IWorkflowTrigger, WorkflowTriggerHandler>();

        // D-034: one consumer set behind both transports. Functions triggers and RabbitMQ handlers resolve these.
        services.AddScoped<TaskProjectionConsumer>();
        services.AddScoped<TaskAiReviewConsumer>();
        services.AddScoped<TaskWorkflowConsumer>();
    }
}
