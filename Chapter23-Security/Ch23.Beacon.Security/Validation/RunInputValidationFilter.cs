using AGUI.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Ch23.Beacon.Security.Validation;

/// <summary>
/// Runs <see cref="RunInputValidator"/> as an endpoint filter, for a host whose handler isn't ours: Agent
/// Framework's <c>MapAGUIServer</c> maps a minimal-API handler, and a filter on its endpoint sees the bound
/// <see cref="RunAgentInput"/> before the handler converts it. No server-side history is consulted here; a
/// session-store host takes only new messages from the client anyway (Chapter 5).
/// </summary>
public sealed class RunInputValidationFilter(RunInputLimits limits) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Fail closed: if a future version binds the input differently, refuse rather than run it unchecked.
        var input = context.Arguments.OfType<RunAgentInput>().FirstOrDefault()
            ?? throw new InvalidOperationException("The endpoint bound no RunAgentInput, so its input can't be validated.");

        var errors = RunInputValidator.Validate(input, limits);
        return errors.Count > 0
            ? ValueTask.FromResult<object?>(Results.ValidationProblem(errors, title: "The run input is not valid."))
            : next(context);
    }
}
