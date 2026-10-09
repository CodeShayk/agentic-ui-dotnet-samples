using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OllamaSharp;
using OpenAI;

namespace Beacon.AI;

/// <summary>
/// Creates the raw <see cref="IChatClient"/> for the configured provider. Callers add middleware
/// (function invocation, logging, telemetry) with <see cref="ChatClientBuilder"/>; this factory
/// only chooses the model.
/// </summary>
public static class AIClientFactory
{
    public static AIOptions ReadOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(AIOptions.SectionName);
        var options = new AIOptions();

        if (Enum.TryParse<AIProvider>(section["Provider"], ignoreCase: true, out var provider))
        {
            options.Provider = provider;
        }

        options.Endpoint = section["Endpoint"] ?? options.Endpoint;
        options.Model = section["Model"] ?? options.Model;
        options.ApiKey = section["ApiKey"] ?? options.ApiKey;
        options.OllamaEndpoint = section["OllamaEndpoint"] ?? options.OllamaEndpoint;
        options.OllamaModel = section["OllamaModel"] ?? options.OllamaModel;

        if (int.TryParse(section["ScriptedChunkDelayMilliseconds"], out var delay))
        {
            options.ScriptedChunkDelayMilliseconds = delay;
        }

        if (int.TryParse(section["TimeoutSeconds"], out var timeout) && timeout > 0)
        {
            options.TimeoutSeconds = timeout;
        }

        return options;
    }

    public static IChatClient CreateChatClient(IConfiguration configuration, IEnumerable<ScriptRule>? scriptRules = null) =>
        CreateChatClient(ReadOptions(configuration), scriptRules);

    public static IChatClient CreateChatClient(AIOptions options, IEnumerable<ScriptRule>? scriptRules = null) =>
        options.Provider switch
        {
            AIProvider.Scripted => new ScriptedChatClient(
                scriptRules ?? BeaconScripts.Default,
                new ScriptedChatClientOptions { ChunkDelay = TimeSpan.FromMilliseconds(options.ScriptedChunkDelayMilliseconds) }),

            AIProvider.Ollama => new OllamaApiClient(
                new HttpClient
                {
                    BaseAddress = new Uri(options.OllamaEndpoint),
                    Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
                },
                options.OllamaModel),

            AIProvider.AzureOpenAI => CreateAzureOpenAIClient(options),

            AIProvider.OpenAI => new OpenAIClient(
                    new ApiKeyCredential(Require(options.ApiKey, "AI:ApiKey")),
                    new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds) })
                .GetChatClient(options.Model ?? "gpt-5-mini")
                .AsIChatClient(),

            _ => throw new InvalidOperationException($"Unknown AI provider '{options.Provider}'."),
        };

    // Azure OpenAI's v1 endpoint speaks the OpenAI API, so the OpenAI SDK serves both.
    // Microsoft Entra ID replaces API keys: DefaultAzureCredential uses your developer sign-in
    // locally and a managed identity in Azure.
    private static IChatClient CreateAzureOpenAIClient(AIOptions options)
    {
        var endpoint = new Uri(new Uri(Require(options.Endpoint, "AI:Endpoint")), "openai/v1/");
        var authentication = new BearerTokenPolicy(
            new DefaultAzureCredential(),
            "https://cognitiveservices.azure.com/.default");

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };

        return new OpenAIClient(authentication, clientOptions)
            .GetChatClient(Require(options.Model, "AI:Model"))
            .AsIChatClient();
    }

    private static string Require(string? value, string key) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Configuration value '{key}' is required for this AI provider.")
            : value;
}
