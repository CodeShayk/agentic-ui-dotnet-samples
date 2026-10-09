using Beacon.Testing;
using Ch26.Beacon.Agent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch26.Tests.Integration.Infrastructure;

/// <summary>
/// Ch26.Beacon.Agent in memory, built exactly as its Program.Main builds it, with one change: the model
/// is a <see cref="RecordedChatClient"/> that answers each call with the next recorded turn. Everything
/// above the model (function invocation, the spending gate, the tools, the AG-UI converter and Beacon's
/// stages) runs for real.
/// </summary>
public sealed class AgentHost(IChatClient model, IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public AgentHost(params IReadOnlyList<ChatResponseUpdate>[] turns)
        : this(new RecordedChatClient(turns))
    {
    }

    /// <summary>The model the agent calls, for assertions about what it was asked.</summary>
    public IChatClient Model { get; } = model;

    /// <summary>The recorded model, when the host replays one.</summary>
    public RecordedChatClient? Recorded => Model as RecordedChatClient;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Logging:LogLevel:Default", "Warning"); // keep test output for test messages

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => services.AddKeyedSingleton(Program.ModelKey, Model));
    }

    /// <summary>A host that replays the named recordings, turn after turn, in the order given.</summary>
    public static async Task<AgentHost> ReplayingAsync(params string[] recordings) =>
        new(await Recordings.TurnsAsync(recordings));
}
