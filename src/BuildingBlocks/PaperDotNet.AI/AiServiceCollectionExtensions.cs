using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace PaperDotNet.AI;

/// <summary>Where embeddings come from (<c>AI:Embeddings</c>).</summary>
public sealed class EmbeddingOptions
{
    public const string Section = "AI:Embeddings";

    /// <summary><c>none</c> (default: no semantic search) or <c>openai</c> (any OpenAI-compatible API).</summary>
    public string Provider { get; set; } = "none";

    /// <summary>
    /// Base URL of the API, e.g. <c>https://api.openai.com/v1</c> or a local server such as Ollama
    /// (<c>http://ollama:11434/v1</c>), LM Studio, vLLM or llama.cpp.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>The embedding model, e.g. <c>text-embedding-3-small</c> or <c>nomic-embed-text</c>.</summary>
    public string? Model { get; set; }

    /// <summary>API key; local servers usually need none.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Requested vector size (only for models that support it); null for the model's default.</summary>
    public int? Dimensions { get; set; }
}

/// <summary>
/// The chat model for AI activities of workflows (<c>AI:Chat</c>, ADR-0036): classify, extract, summarize, prompt. Off by
/// default. The Workflows module reads its limits (daily tokens, input size, cache) from the same section.
/// </summary>
public sealed class ChatModelOptions
{
    public const string Section = "AI:Chat";

    /// <summary><c>none</c> (default: AI activities fail with a clear error) or <c>openai</c> (any OpenAI-compatible API).</summary>
    public string Provider { get; set; } = "none";

    /// <summary>Base URL of the API, e.g. <c>https://api.openai.com/v1</c>, Azure OpenAI's <c>…/openai/v1/</c> or a local server.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>The model (or deployment), e.g. <c>gpt-4o-mini</c> or <c>llama3.1</c>.</summary>
    public string? Model { get; set; }

    /// <summary>API key; local servers usually need none.</summary>
    public string? ApiKey { get; set; }
}

public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> when <c>AI:Embeddings:Provider</c> is set
    /// (with logging and OpenTelemetry); nothing otherwise, so features that need it stay off.
    /// </summary>
    public static IServiceCollection AddPaperDotNetAI(this IServiceCollection services, IConfiguration configuration)
    {
        AddChat(services, configuration);
        var options = configuration.GetSection(EmbeddingOptions.Section).Get<EmbeddingOptions>() ?? new EmbeddingOptions();
        switch (options.Provider.ToLowerInvariant())
        {
            case "none" or "":
                return services;
            case "openai":
                if (options.Model is not { Length: > 0 } model)
                {
                    throw new InvalidOperationException($"{EmbeddingOptions.Section}:Model is required for the provider 'openai'.");
                }

                var client = new OpenAIClient(
                    new ApiKeyCredential(options.ApiKey is { Length: > 0 } key ? key : "none"),
                    new OpenAIClientOptions { Endpoint = options.Endpoint ?? new Uri("https://api.openai.com/v1") });
                services.AddEmbeddingGenerator(client.GetEmbeddingClient(model).AsIEmbeddingGenerator(options.Dimensions))
                    .UseLogging()
                    .UseOpenTelemetry();
                return services;
            default:
                throw new InvalidOperationException($"Unknown {EmbeddingOptions.Section}:Provider '{options.Provider}' (use none or openai).");
        }
    }

    /// <summary>Registers <see cref="IChatClient"/> when <c>AI:Chat:Provider</c> is set (with logging and OpenTelemetry).</summary>
    private static void AddChat(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ChatModelOptions.Section).Get<ChatModelOptions>() ?? new ChatModelOptions();
        switch (options.Provider.ToLowerInvariant())
        {
            case "none" or "":
                return;
            case "openai":
                if (options.Model is not { Length: > 0 } model)
                {
                    throw new InvalidOperationException($"{ChatModelOptions.Section}:Model is required for the provider 'openai'.");
                }

                var client = new OpenAIClient(
                    new ApiKeyCredential(options.ApiKey is { Length: > 0 } key ? key : "none"),
                    new OpenAIClientOptions { Endpoint = options.Endpoint ?? new Uri("https://api.openai.com/v1") });
                services.AddChatClient(client.GetChatClient(model).AsIChatClient())
                    .UseLogging()
                    .UseOpenTelemetry();
                return;
            default:
                throw new InvalidOperationException($"Unknown {ChatModelOptions.Section}:Provider '{options.Provider}' (use none or openai).");
        }
    }
}
