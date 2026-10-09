using Beacon.AI;

var builder = WebApplication.CreateBuilder(args);

// AG-UI's JSON configuration and the Server-Sent Events formatter.
builder.Services.AddAGUIHosting();

// The model behind the endpoint. The default provider is Beacon's scripted model, so this
// runs offline; Chapter 5 builds a real pipeline around it.
builder.Services.AddChatClient(AIClientFactory.CreateChatClient(builder.Configuration));

var app = builder.Build();

// POST /agent: RunAgentInput in, a stream of AG-UI events out.
app.MapAGUIEndpoint("/agent");

app.Run();

// Lets WebApplicationFactory<Program> host this agent in the tests.
public partial class Program;
