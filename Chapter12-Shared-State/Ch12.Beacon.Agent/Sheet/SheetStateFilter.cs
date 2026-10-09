using AGUI.Abstractions;
using Beacon.Domain;

namespace Ch12.Beacon.Agent.Sheet;

/// <summary>
/// Rejects a run whose state or context breaks the sheet's contract, before the run starts: a 400 with
/// problem details keyed by JSON path, no <c>RUN_STARTED</c>, no model call. Malformed input is the
/// client's mistake (or an attack), and the transport's error path is where AG-UI says it belongs.
/// </summary>
internal sealed class SheetStateFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.Arguments.OfType<RunAgentInput>().FirstOrDefault() is { } input)
        {
            var store = context.HttpContext.RequestServices.GetRequiredService<IFieldServiceStore>();
            var errors = SheetValidator.Validate(input, store);
            if (errors.Count > 0)
            {
                return TypedResults.ValidationProblem(errors, title: "The shared state or context was rejected.");
            }
        }

        return await next(context);
    }
}
