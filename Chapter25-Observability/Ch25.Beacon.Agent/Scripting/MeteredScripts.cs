using System.Text.Json;
using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch25.Beacon.Agent.Scripting;

/// <summary>
/// Makes the scripted model report token usage the way a real provider does: one
/// <see cref="UsageContent"/> at the end of every reply. The counts are estimates (four characters
/// to a token) because the scripted model has no tokenizer; real providers report what they charged.
/// </summary>
public static class MeteredScripts
{
    /// <summary>Wraps every rule so its reply carries usage, and adds a metered fallback and summary.</summary>
    public static IReadOnlyList<ScriptRule> WithUsage(IEnumerable<ScriptRule> rules) =>
    [
        .. rules.Select(rule => rule with { Reply = Metered(rule.Reply), Summarize = Metered(rule.Summarize) }),

        // The scripted model's built-in fallback and tool-result summary, metered too.
        new("metered-fallback",
            Reply: context => AddUsage(context, BeaconScripts.Fallback),
            Summarize: context => AddUsage(context, BeaconScripts.DefaultSummary(context))),
    ];

    private static Func<ScriptContext, ScriptReply?>? Metered(Func<ScriptContext, ScriptReply?>? rule) =>
        rule is null ? null : context => rule(context) is { } reply ? AddUsage(context, reply) : null;

    /// <summary>Appends a usage report to a reply, through <see cref="ScriptReply.ExtraContents"/>.</summary>
    public static ScriptReply AddUsage(ScriptContext context, ScriptReply reply)
    {
        // Input: everything the call sent the model. Output: everything it generated, reasoning included.
        var input = Tokens(context.Options?.Instructions)
            + context.Messages.SelectMany(m => m.Contents).Sum(Tokens)
            + (context.Options?.Tools ?? []).Sum(tool => Tokens(tool.Name) + Tokens(tool.Description)
                + (tool is AIFunctionDeclaration function ? Tokens(function.JsonSchema.GetRawText()) : 0));

        var reasoning = Tokens(reply.Reasoning);
        var output = reasoning + Tokens(reply.Text)
            + reply.ToolCalls.Where(call => context.HasTool(call.ToolName))
                .Sum(call => Tokens(call.ToolName) + Tokens(Serialize(call.Arguments)));

        var usage = new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            TotalTokenCount = input + output,
            ReasoningTokenCount = reasoning > 0 ? reasoning : null,
        };

        return reply with { ExtraContents = [.. reply.ExtraContents, new UsageContent(usage)] };
    }

    private static long Tokens(AIContent content) => content switch
    {
        TextContent text => Tokens(text.Text),
        TextReasoningContent reasoning => Tokens(reasoning.Text),
        FunctionCallContent call => Tokens(call.Name) + Tokens(Serialize(call.Arguments)),
        FunctionResultContent result => Tokens(ScriptedChatClient.ResultJson(result)),
        _ => 0,
    };

    private static long Tokens(string? text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    private static string Serialize(IDictionary<string, object?>? arguments) =>
        arguments is null ? string.Empty : JsonSerializer.Serialize(arguments, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, object?>)));
}
