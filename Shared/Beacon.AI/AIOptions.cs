namespace Beacon.AI;

/// <summary>Which model sits behind Beacon's <c>IChatClient</c>.</summary>
public enum AIProvider
{
    /// <summary>A deterministic, offline model that follows Beacon's scripts. The default; needs no keys.</summary>
    Scripted,

    /// <summary>A local model served by Ollama.</summary>
    Ollama,

    /// <summary>Azure OpenAI in Microsoft Foundry, authenticated with Microsoft Entra ID.</summary>
    AzureOpenAI,

    /// <summary>OpenAI's public API.</summary>
    OpenAI,
}

/// <summary>Model settings, read from the <c>AI</c> configuration section.</summary>
public sealed class AIOptions
{
    public const string SectionName = "AI";

    public AIProvider Provider { get; set; } = AIProvider.Scripted;

    /// <summary>Azure OpenAI resource endpoint, for example <c>https://contoso.openai.azure.com/</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Azure OpenAI deployment name or OpenAI model name.</summary>
    public string? Model { get; set; }

    /// <summary>OpenAI API key. Keep it in User Secrets or a secret store, never in appsettings.json.</summary>
    public string? ApiKey { get; set; }

    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    public string OllamaModel { get; set; } = "llama3.2";

    /// <summary>The pause between streamed chunks from the scripted model, so streaming is visible.</summary>
    public int ScriptedChunkDelayMilliseconds { get; set; } = 25;

    /// <summary>
    /// How long one model call may take. Large local models can need several minutes for a first
    /// token, so the default is more generous than <see cref="HttpClient"/>'s 100 seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
}
