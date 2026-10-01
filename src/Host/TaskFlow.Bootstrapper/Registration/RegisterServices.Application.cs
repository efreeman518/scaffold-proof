using EF.BackgroundServices.InternalMessageBus;
using EF.Common.Contracts;
using EF.Data.Contracts;
using EF.Data.Encryption;
using EF.Messaging;
using EF.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Cryptography;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Cqrs.Registration;
using TaskFlow.Application.MessageHandlers;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Application.Services;

namespace TaskFlow.Bootstrapper;

/// <summary>Configures register services host behavior for TaskFlow runtime services.</summary>
public static partial class RegisterServices
{
    /// <summary>Registers application services dependencies in the service container.</summary>
    private static void AddApplicationServices(IServiceCollection services, IConfiguration config)
    {
        AddMessageHandlers(services, config);
        AddVectorSearchServices(services, config);
        AddSharedApplicationServices(services);
        AddServiceApplicationServices(services);

        if (ApplicationStyleResolver.Resolve(config[ApplicationStyleResolver.ConfigKey]) == ApplicationStyle.Cqrs)
        {
            services.AddTaskFlowCqrsApplication();
        }

        services.AddScoped<ITaskViewProjectionService, TaskViewProjectionService>();
    }

    /// <summary>
    /// D14 / D-067: EF.Tenancy's singleton boundary validator. GlobalAdmin (a real admin token) and System (the
    /// no-request context of <see cref="AddRequestContext"/>, which no token can claim) pass the boundary, so
    /// consumers and jobs act for any tenant.
    /// </summary>
    internal static void AddTenantBoundary(IServiceCollection services) =>
        services.AddTenancy(options => options.CrossTenantRoles = [AppConstants.ROLE_GLOBAL_ADMIN, AppConstants.ROLE_SYSTEM]);

    /// <summary>Registers shared application services dependencies in the service container.</summary>
    private static void AddSharedApplicationServices(IServiceCollection services)
    {
        AddTenantBoundary(services);

        // Documented exception to the Service/CQRS split: the aggregate read model (summary, metadata,
        // export) is a pure projection with no domain behavior to duplicate, so both styles share it.
        services.AddScoped<ITaskFlowReadService, TaskFlowReadService>();

        // Idempotent - the web host may already have configured a persisted key ring (Program.cs).
        // Still required after the cursor protector was retired (D-043): antiforgery and the Blazor
        // server-side pipeline resolve IDataProtectionProvider.
        services.AddDataProtection();
        services.AddSingleton(sp => new CursorCodec(CursorSigningKey(sp.GetRequiredService<ColumnEncryptionKeys>())));
    }

    /// <summary>
    /// Signing key for <see cref="CursorCodec"/> (package request 27). HKDF over the column-encryption DEK
    /// under its own info label rather than a new configured secret: the DEK is already required and shared
    /// by every replica, and a second key to rotate is a second thing to get wrong. Rotating the DEK
    /// invalidates outstanding cursors, which is the intended effect of a rotation.
    /// <para>
    /// With <c>Database:Encryption:Enabled=false</c> there is no shared secret to derive from, so cursors
    /// are signed with a per-process key and stop validating after a restart or on a sibling replica (400,
    /// never a silent reset to page one) - the same caveat the in-memory Data Protection ring carried.
    /// </para>
    /// </summary>
    private static byte[] CursorSigningKey(ColumnEncryptionKeys keys) =>
        keys.DataEncryptionKey is byte[] dek
            ? HKDF.DeriveKey(HashAlgorithmName.SHA256, dek, CursorCodec.SignatureSizeBytes, info: "TaskFlow.Cursor.v1"u8.ToArray())
            : RandomNumberGenerator.GetBytes(CursorCodec.SignatureSizeBytes);

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
    private static void AddMessageHandlers(IServiceCollection services, IConfiguration config)
    {
        // D-029 claim timings (lease, renewal ceiling, poll, wait margin); the package defaults suit RabbitMQ and
        // Service Bus alike, MaxClaimDuration (10 min) included, so none is set here.
        services.AddOptions<InboxClaimOptions>()
            .Bind(config.GetSection(InboxClaimOptions.ConfigSectionName))
            .Validate(o => o.IsValid(), "Messaging:Inbox timings must have a positive ClaimLease, a MaxClaimDuration above ClaimLease, a non-negative WaitMargin and a positive WaitPollInterval below WaitBound.")
            .ValidateOnStart();
        // D-034/D-048: one envelope reader configuration for the Service Bus triggers and the RabbitMQ handlers.
        services.Configure<IntegrationEnvelopeReaderOptions>(TaskFlowIntegrationEvents.ConfigureReader);
        services.TryAddSingleton<MessagingMetrics>();

        services.AddScoped<IMessageHandler<AuditEntry<string, Guid>>, AuditHandler>();
        services.AddScoped<IMessageHandler<AuditEntry<string, Guid?>>, AuditHandler>();
        services.AddScoped<IWorkflowTrigger, WorkflowTriggerHandler>();

        // D-034: one consumer set behind both transports. Functions triggers and RabbitMQ handlers resolve these.
        services.AddScoped<TaskProjectionConsumer>();
        services.AddScoped<TaskAiReviewConsumer>();
        services.AddScoped<TaskWorkflowConsumer>();
    }
}
