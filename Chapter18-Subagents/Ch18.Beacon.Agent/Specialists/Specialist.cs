using System.Text.Json;
using AGUI.Abstractions;
using Ch18.Beacon.Agent.Approvals;
using Microsoft.Extensions.AI;

namespace Ch18.Beacon.Agent.Specialists;

/// <summary>
/// One of Beacon's specialists: a subagent with its own instructions, its own tools and its own chat
/// pipeline (function invocation, the spending policy, logging) over the shared model. The coordinator
/// reaches it through a delegation tool its own model can call, <see cref="DelegationTool"/>.
/// </summary>
/// <param name="info">The specialist's reusable name and description, as <c>SUBAGENT_STARTED</c> reports them.</param>
/// <param name="label">What a person calls it: "Parts specialist".</param>
/// <param name="instructions">Its system instructions. They live on the server, like the coordinator's.</param>
/// <param name="tools">The only tools it may call.</param>
/// <param name="client">Its pipeline, ending in the model.</param>
/// <param name="timeout">How long one invocation may take before the coordinator gives up on it.</param>
public sealed class Specialist(SubagentInfo info, string label, string instructions, IReadOnlyList<AITool> tools, IChatClient client, TimeSpan timeout)
{
    private static readonly JsonElement DelegationSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "task": { "type": "string", "description": "What the specialist should do, in one or two sentences, naming the work order." }
          },
          "required": ["task"]
        }
        """).RootElement.Clone();

    public SubagentInfo Info { get; } = info;

    public string Name => Info.Name;

    public string Label { get; } = label;

    public string Instructions { get; } = instructions;

    public IReadOnlyList<AITool> Tools { get; } = tools;

    public IChatClient Client { get; } = client;

    public TimeSpan Timeout { get; } = timeout;

    /// <summary>The coordinator's tool for this specialist: <c>ask_parts_specialist</c> and so on.</summary>
    public string DelegationToolName { get; } = "ask_" + info.Name.Replace('-', '_');

    /// <summary>A declaration only: the coordinator runs the delegation itself, so it can stream the specialist's work.</summary>
    public AIFunctionDeclaration DelegationTool => AIFunctionFactory.CreateDeclaration(DelegationToolName, Info.Description ?? Label, DelegationSchema);

    /// <summary>The options for one invocation: its instructions, its tools and the run its approvals belong to.</summary>
    public ChatOptions OptionsFor(ApprovalRun run) => new()
    {
        Instructions = Instructions,
        Tools = [.. Tools],
        AdditionalProperties = new AdditionalPropertiesDictionary { [ApprovalRun.Key] = run },
    };
}
