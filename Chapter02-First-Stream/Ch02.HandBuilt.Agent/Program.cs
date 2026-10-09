using System.Text.Json.Serialization;
using Beacon.AI;
using Ch02.HandBuilt.Agent;
using Ch02.HandBuilt.Agent.Protocol;

var builder = WebApplication.CreateBuilder(args);

// The request body and the events use our source-generated JSON: camelCase, no nulls.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AgentJsonContext.Default);
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// The model behind the endpoint: Beacon's scripted model unless the AI section says otherwise.
builder.Services.AddChatClient(AIClientFactory.CreateChatClient(builder.Configuration));
builder.Services.AddSingleton<AgentRunner>();

var app = builder.Build();

// One POST per run, answered with a Server-Sent Events stream of AG-UI events.
app.MapPost("/agent", (RunAgentInput input, AgentRunner runner, HttpContext httpContext) =>
    TypedResults.ServerSentEvents(runner.StreamAsync(input, httpContext.RequestAborted)));

app.Run();
