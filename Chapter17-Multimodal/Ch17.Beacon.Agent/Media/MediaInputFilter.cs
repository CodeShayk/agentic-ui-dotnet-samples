using AGUI.Abstractions;
using Ch17.Media;

namespace Ch17.Beacon.Agent.Media;

/// <summary>
/// An endpoint filter in front of the AG-UI endpoint: it runs after ASP.NET Core has bound the
/// <see cref="RunAgentInput"/> and before the handler converts it, and refuses media that breaks Beacon's
/// input rules with a 400 that names each problem. The run never starts. Some of those values pass the
/// schema, so the 400 is Beacon's policy, stricter than the specification (see <see cref="MediaInputRules"/>).
/// </summary>
public sealed class MediaInputFilter(MediaPolicy policy) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.Arguments.OfType<RunAgentInput>().FirstOrDefault() is { } input
            && MediaInputRules.Validate(input, policy) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors, title: "The run's attachments can't be accepted.");
        }

        return await next(context).ConfigureAwait(false);
    }
}
