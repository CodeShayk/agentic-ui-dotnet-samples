// Adapted from the AG-UI .NET SDK samples (AGUI.Samples.Shared), MIT License.
using AGUI.Abstractions;
using AGUI.Formatting;
using AGUI.Protobuf;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class AGUIHostingServiceCollectionExtensions
{
    /// <summary>
    /// Registers what an AG-UI endpoint needs: the Server-Sent Events formatter and the JSON
    /// configuration that lets ASP.NET Core read <see cref="RunAgentInput"/> and write AG-UI events.
    /// </summary>
    public static IServiceCollection AddAGUIHosting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAGUIEventStreamFormatter, SseEventStreamFormatter>());

        services.Configure<JsonOptions>(options =>
        {
            // The AG-UI resolver must come first. It omits unset optional fields (the 1.0 wire
            // format forbids "parentRunId": null and friends), and Microsoft.Extensions.AI's
            // resolver would otherwise claim AG-UI types and write those nulls.
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver);
            options.SerializerOptions.TypeInfoResolverChain.Add(AIJsonUtilities.DefaultOptions.TypeInfoResolver!);

            // Lets interrupt and approval content round-trip through RunAgentInput.resume.
            AGUIJsonUtilities.RegisterInterruptContentTypes(options.SerializerOptions);
        });

        return services;
    }

    /// <summary>
    /// Adds the protobuf formatter so clients that send
    /// <c>Accept: application/vnd.ag-ui.event+proto</c> get the binary encoding (Chapter 24).
    /// </summary>
    public static IServiceCollection AddAGUIProtobuf(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAGUIEventStreamFormatter, ProtobufEventStreamFormatter>());
        return services;
    }
}
