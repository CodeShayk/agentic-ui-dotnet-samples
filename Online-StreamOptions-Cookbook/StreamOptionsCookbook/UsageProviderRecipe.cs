using AGUI.Server;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook;

/// <summary>Recipe: <see cref="AGUIStreamOptions.WithUsageProvider"/> names the provider on the run's token usage.</summary>
public static class UsageProviderRecipe
{
    /// <summary>
    /// Labels the usage on <c>RUN_FINISHED</c> (or <c>RUN_ERROR</c>) with the provider the chat client reports
    /// about itself, so the wire and the <c>gen_ai.provider.name</c> span attribute agree. The model label
    /// needs no help: it comes from each update's <see cref="ChatResponseUpdate.ModelId"/>.
    /// </summary>
    public static AGUIStreamOptions Apply(AGUIStreamOptions options, IChatClient chatClient) => options
        .WithUsageProvider(chatClient.GetService<ChatClientMetadata>()?.ProviderName);
}
