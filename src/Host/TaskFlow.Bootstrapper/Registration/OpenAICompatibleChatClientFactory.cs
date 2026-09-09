using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using System.ClientModel;
using TaskFlow.Infrastructure.AI;

namespace TaskFlow.Bootstrapper;

/// <summary>
/// Builds the OpenAI SDK client for the OpenAICompatible LLM arm (D-041): one client against a
/// configurable endpoint + API key covers OpenAI, OpenRouter, Ollama, vLLM, and any other
/// OpenAI-wire-protocol gateway, instead of one integration per vendor.
/// </summary>
internal static class OpenAICompatibleChatClientFactory
{
    public const string EndpointConfigKey = "AiServices:Endpoint";
    public const string ApiKeyConfigKey = "AiServices:ApiKey";
    public const string ChatModelConfigKey = "AiServices:ChatModel";
    public const string EmbeddingModelConfigKey = "AiServices:EmbeddingModel";

    /// <summary>Chat client and embedding generator built from the same OpenAI-compatible endpoint.</summary>
    internal readonly record struct Clients(
        IChatClient ChatClient,
        IEmbeddingGenerator<string, Embedding<float>> EmbeddingGenerator,
        string Endpoint);

    /// <summary>
    /// Reads <see cref="EndpointConfigKey"/>/<see cref="ApiKeyConfigKey"/> and builds both clients.
    /// Fails fast (<see cref="InvalidOperationException"/>) when the endpoint or API key is missing:
    /// an explicitly selected provider with no way to reach it is a configuration bug, not a fallback
    /// case. Chat/embedding model names fall back to <see cref="TaskFlowAiSettings.AgentModelDeployment"/>/
    /// <see cref="TaskFlowAiSettings.EmbeddingModelDeployment"/> so one deployment-name pair still works
    /// for every provider arm.
    /// </summary>
    internal static Clients Create(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var endpoint = config[EndpointConfigKey];
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException(
                $"AI provider OpenAICompatible requires {EndpointConfigKey}.");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            throw new InvalidOperationException($"{EndpointConfigKey} must be an absolute URI.");

        var apiKey = config[ApiKeyConfigKey];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                $"AI provider OpenAICompatible requires {ApiKeyConfigKey}.");

        var aiSettings = config.GetSection(TaskFlowAiSettings.ConfigSectionName).Get<TaskFlowAiSettings>()
            ?? new TaskFlowAiSettings();
        var chatModel = config[ChatModelConfigKey] is { Length: > 0 } chat ? chat : aiSettings.AgentModelDeployment;
        var embeddingModel = config[EmbeddingModelConfigKey] is { Length: > 0 } embed
            ? embed
            : aiSettings.EmbeddingModelDeployment;

        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = endpointUri });

        return new Clients(
            client.GetChatClient(chatModel).AsIChatClient(),
            client.GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator(),
            endpointUri.ToString());
    }
}
