using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskFlow.Infrastructure.AI;

namespace TaskFlow.Bootstrapper;

/// <summary>LLM provider selected for this deployment (D-041).</summary>
public enum AiProvider
{
    /// <summary>Azure AI Foundry via the Aspire-injected "chat" connection.</summary>
    AzureInference,

    /// <summary>OpenAI SDK client against a configurable endpoint (OpenAI, OpenRouter, Ollama, vLLM).</summary>
    OpenAICompatible,

    /// <summary>Foundry Local SDK-direct fallback.</summary>
    FoundryLocal,

    /// <summary>No live chat client; NoOpChatClient answers every call.</summary>
    None
}

public static partial class RegisterServices
{
    public const string AiProviderConfigKey = "AiServices:Provider";
    public const string AiProviderEnvVar = "TASKFLOW_AI_PROVIDER";

    /// <summary>
    /// Resolves an explicit AI provider selection. Null means "unset": the caller derives today's default
    /// (ConnectionStrings:chat present -> AzureInference, else FoundryLocal) or the Portable lane default
    /// (OpenAICompatible) itself (D-035).
    /// </summary>
    public static AiProvider? ResolveAiProvider(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var value = Environment.GetEnvironmentVariable(AiProviderEnvVar) ?? config[AiProviderConfigKey];
        if (!string.IsNullOrWhiteSpace(value)) return ParseAiProvider(value);

        return HostingLaneSelector.Resolve(config) == HostingLane.Portable
            ? AiProvider.OpenAICompatible
            : null;
    }

    private static AiProvider ParseAiProvider(string value) =>
        Enum.TryParse<AiProvider>(value, ignoreCase: true, out var provider)
            ? provider
            : throw new ArgumentException(
                $"Unknown AI provider '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<AiProvider>())}.");

    /// <summary>
    /// Registers the shared AI chat client before application services bind agents and demos.
    /// Azure Foundry wins when Aspire injects the chat connection; otherwise Foundry Local is
    /// attempted directly. Local startup failures fall through to the no-op client in AddAiServices.
    /// An explicit <c>AiServices:Provider</c>/<c>TASKFLOW_AI_PROVIDER</c> value overrides this derivation.
    /// </summary>
    public static async Task RegisterAiChatClientAsync(
        this IHostApplicationBuilder builder,
        ILogger logger,
        CancellationToken ct = default)
    {
        var config = builder.Configuration;
        var appName = config.GetValue<string>("AppName") ?? builder.Environment.ApplicationName;
        var env = builder.Environment.EnvironmentName;

        LogLegacyAiSwitchCompatibility(config, logger, appName, env);

        var explicitProvider = ResolveAiProvider(config);
        if (explicitProvider == AiProvider.None)
            return; // AddAiServices registers the no-op chat client and AiProviderInfo("none") fallback.

        if (explicitProvider == AiProvider.OpenAICompatible)
        {
            var clients = OpenAICompatibleChatClientFactory.Create(config);
            logger.ConfigureOpenAICompatibleChatClient(appName, env, clients.Endpoint);
            builder.Services.AddSingleton(clients.ChatClient);
            builder.Services.AddSingleton(clients.EmbeddingGenerator);
            builder.Services.AddSingleton(new AiProviderInfo("openai-compatible"));
            return;
        }

        var chatConnection = config.GetConnectionString("chat");
        var useAzure = explicitProvider == AiProvider.AzureInference
            || (explicitProvider is null && !string.IsNullOrWhiteSpace(chatConnection));
        if (useAzure)
        {
            logger.ConfigureAzureChatClient(appName, env);
            builder.AddAzureChatCompletionsClient("chat")
                .AddChatClient();

            // Trivial to add: Aspire.Azure.AI.Inference exposes AddEmbeddingGenerator() the same way as
            // AddChatClient(). No "embeddings" deployment exists in AppHost yet (P6 territory), so this
            // stays inert until ConnectionStrings:embeddings is configured; P7's IEmbeddingGenerator
            // resolution fails fast until then, which is the intended default-arm behavior for an
            // unconfigured optional dependency.
            var embeddingConnection = config.GetConnectionString("embeddings");
            if (!string.IsNullOrWhiteSpace(embeddingConnection))
            {
                builder.AddAzureEmbeddingsClient("embeddings").AddEmbeddingGenerator();
            }

            builder.Services.AddSingleton(new AiProviderInfo("azure"));
            return;
        }

        // FoundryLocal, explicit or derived. The legacy kill switch only applies to the derived (unset)
        // path - an explicit FoundryLocal selection means try it regardless.
        if (explicitProvider is null && config.GetValue<bool>("AiServices:DisableFoundryLocal"))
            return;

        logger.ConfigureFoundryLocalChatClient(appName, env);
        var requireFoundryLocal = config.GetValue<bool>("AiServices:RequireFoundryLocal");
        var localModel = config["AiServices:LocalModel"] ?? "qwen2.5-0.5b";
        var localWebUrl = config["AiServices:LocalWebUrl"] ?? "http://127.0.0.1:52415";

        try
        {
            if (!Uri.TryCreate(localWebUrl, UriKind.Absolute, out _))
            {
                throw new ArgumentException("Foundry Local web URL (AiServices:LocalWebUrl) must be absolute.");
            }

            var chatClient = await FoundryLocalChatClient.CreateAsync(
                localModel,
                localWebUrl,
                logger,
                ct);
            builder.Services.AddSingleton<IChatClient>(chatClient);
            builder.Services.AddSingleton(new AiProviderInfo("local"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (requireFoundryLocal)
                throw;

            logger.LogWarning(
                ex,
                "{AppName} {Environment} - Foundry Local unavailable. Falling back to no-op AI client.",
                appName,
                env);
        }
    }

    /// <summary>
    /// D-042 compatibility: <c>AiServices:UseSearch</c>/<c>UseAgents</c> are the ad hoc kill switches
    /// P5's dynamic feature flags supersede. Kept working (P2 left the properties in place) but warned
    /// about for one release when a config still sets either to false explicitly - unset stays silent,
    /// since that is the common case and not a signal anyone still relies on the old switch.
    /// </summary>
    private static void LogLegacyAiSwitchCompatibility(IConfiguration config, ILogger logger, string appName, string env)
    {
        if (bool.TryParse(config["AiServices:UseSearch"], out var useSearch) && !useSearch)
            logger.LegacyAiSwitchSuperseded(appName, env, "AiServices:UseSearch");

        if (bool.TryParse(config["AiServices:UseAgents"], out var useAgents) && !useAgents)
            logger.LegacyAiSwitchSuperseded(appName, env, "AiServices:UseAgents");
    }
}
