// Adapted from the AG-UI .NET SDK samples (AGUI.Samples.Shared), MIT License.
using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AGUI.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Microsoft.AspNetCore.Builder;

public static class AGUIEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a POST endpoint that runs the registered <see cref="IChatClient"/> for each
    /// <see cref="RunAgentInput"/> and streams the result as AG-UI events.
    /// </summary>
    public static IEndpointConventionBuilder MapAGUIEndpoint(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Action<AGUIEndpointOptions>? configure = null)
    {
        var endpointOptions = new AGUIEndpointOptions();
        configure?.Invoke(endpointOptions);
        var protocolVersion = endpointOptions.ProtocolVersion;
        if (protocolVersion is not null && !AGUIEventStreamExtensions.IsVersionIdentifier(protocolVersion))
        {
            throw new ArgumentException($"'{protocolVersion}' isn't an AG-UI version identifier (MAJOR.MINOR, such as \"1.0\").", nameof(configure));
        }

        return endpoints.MapPost(pattern, (
            [FromBody] RunAgentInput input,
            IOptions<JsonOptions> jsonOptions,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var chatClient = endpointOptions.ChatClientKey is { } key
                ? httpContext.RequestServices.GetRequiredKeyedService<IChatClient>(key)
                : httpContext.RequestServices.GetRequiredService<IChatClient>();

            var streamOptions = endpointOptions.StreamOptions?.Invoke(httpContext);

            // 1. Adapt the AG-UI request to Microsoft.Extensions.AI: messages, options, client tools.
            var context = input.ToChatRequestContext(jsonOptions.Value.SerializerOptions, streamOptions);

            // 2. Run the agent.
            var updates = chatClient.GetStreamingResponseAsync(context.Messages, context.ChatOptions, cancellationToken);

            // 3. Convert the update stream to AG-UI events, declaring the protocol version if asked to.
            var events = updates.AsAGUIEventStreamAsync(context, cancellationToken);
            if (protocolVersion is not null)
            {
                events = events.WithProtocolVersion(protocolVersion);
            }

            // 4. Write them in the format the client accepts (SSE by default).
            return AGUIResults.Events(events, httpContext, cancellationToken);
        });
    }
}

/// <summary>Per-endpoint settings for <see cref="AGUIEndpointRouteBuilderExtensions.MapAGUIEndpoint"/>.</summary>
public sealed class AGUIEndpointOptions
{
    /// <summary>The DI key of the <see cref="IChatClient"/> to run; <see langword="null"/> uses the default registration.</summary>
    public object? ChatClientKey { get; set; }

    /// <summary>Builds the <see cref="AGUIStreamOptions"/> for a request (state mapping, interrupts, usage).</summary>
    public Func<HttpContext, AGUIStreamOptions>? StreamOptions { get; set; }

    /// <summary>
    /// The AG-UI version this endpoint declares on every <c>RUN_STARTED</c>: normally
    /// <see cref="AGUIProtocol.Version"/> (<c>"1.0"</c>), a <c>MAJOR.MINOR</c> identifier per the spec's
    /// versioning rules. <c>AGUI.Server</c> 1.0.0 never sets <c>protocolVersion</c>, so the endpoint
    /// stamps it on any <c>RUN_STARTED</c> that doesn't carry one. <see langword="null"/>, the default,
    /// leaves the stream exactly as the SDK writes it.
    /// </summary>
    public string? ProtocolVersion { get; set; }
}
