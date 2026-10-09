using AGUI.Abstractions;
using Ch03.EventTour;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Read RunAgentInput with the protocol's own JSON contract: camelCase names, polymorphic messages
// and content parts, and optional fields omitted rather than null.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver));
builder.Services.Configure<TourOptions>(builder.Configuration.GetSection(TourOptions.SectionName));

var app = builder.Build();

// One POST, one stream. The last user message picks the scripted run (see Tour.Scenarios).
app.MapPost("/agent", (RunAgentInput input, IOptions<TourOptions> options, ILogger<TourScenario> logger, HttpContext http) =>
{
    var scenario = Tour.Choose(input);
    logger.LogInformation("Run {RunId} on thread {ThreadId}: playing the '{Scenario}' scenario", input.RunId, input.ThreadId, scenario.Name);

    return TypedResults.ServerSentEvents(Tour.PlayAsync(scenario, input, options.Value, http.RequestAborted));
});

app.Run();

/// <summary>Visible to WebApplicationFactory in the tests.</summary>
public partial class Program;
