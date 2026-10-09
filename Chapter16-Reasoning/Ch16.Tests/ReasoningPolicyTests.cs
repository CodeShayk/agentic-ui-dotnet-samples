using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Server;
using Beacon.Client.Core;
using Beacon.Testing;
using Ch16.Beacon.Agent.Reasoning;
using Ch16.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ch16.Tests;

/// <summary>
/// The policy layer against provider streams the scripted model doesn't produce: redaction across chunk
/// boundaries, values with no visible reasoning, values in the same content as their text, and updates that
/// mix reasoning with an answer. Plus the SDK behavior that makes the anchoring necessary.
/// </summary>
public sealed class ReasoningPolicyTests
{
    [Fact]
    public async Task A_phone_number_split_across_deltas_is_still_redacted()
    {
        var shaped = await ShapeAsync(ReasoningDisclosure.Full,
            Update(new TextReasoningContent("If the coil is clean, call the site on 555-")),
            Update(new TextReasoningContent("0141 or write to facilities@harborview.example. ")),
            Update(new TextReasoningContent("Then check the fan.")),
            Update(new TextContent("Clean the coil.")));

        var reasoning = string.Concat(shaped.SelectMany(u => u.Contents).OfType<TextReasoningContent>().Select(r => r.Text));
        Assert.Equal("If the coil is clean, call the site on [phone number removed] or write to [email removed]. Then check the fan.", reasoning);
    }

    [Fact]
    public async Task Redaction_is_a_safety_net_names_addresses_and_other_formats_pass_through()
    {
        // The scope the chapter states: North American phone numbers, email addresses and card-like digit runs.
        var shaped = await ShapeAsync(ReasoningDisclosure.Full,
            Update(new TextReasoningContent("Dr. Patel at 14 Harbor Road, +44 20 7946 0958, seems careless with the filters. ")),
            Update(new TextReasoningContent("Call 207-555-0141, write to facilities@harborview.example or bill 4111 1111 1111 1111. ")),
            Update(new TextContent("Clean the coil.")));

        var reasoning = string.Concat(shaped.SelectMany(u => u.Contents).OfType<TextReasoningContent>().Select(r => r.Text));
        Assert.Contains("Call [phone number removed], write to [email removed] or bill [card number removed].", reasoning, StringComparison.Ordinal);

        // Everything else is the disclosure setting's job: Summary from a summarizing provider, or Hidden.
        Assert.Contains("Dr. Patel at 14 Harbor Road, +44 20 7946 0958, seems careless", reasoning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hidden_keeps_the_sealed_value_and_gives_it_a_message_of_its_own()
    {
        var shaped = await ShapeAsync(ReasoningDisclosure.Hidden,
            Update(new TextReasoningContent("Private chain of thought. ")),
            Update(new TextReasoningContent(null) { ProtectedData = "sealed-1" }),
            Update(new TextContent("Clean the coil.")));

        Assert.Empty(shaped.SelectMany(u => u.Contents).OfType<TextReasoningContent>());
        var events = shaped.Select(u => u.RawRepresentation).OfType<BaseEvent>().ToList();
        Assert.Equal(["REASONING_START", "REASONING_MESSAGE_START", "REASONING_ENCRYPTED_VALUE", "REASONING_MESSAGE_END", "REASONING_END"], AGUIWire.Types(events));
        var value = events.OfType<ReasoningEncryptedValueEvent>().Single();
        Assert.Equal(("message", "sealed-1"), (value.Subtype, value.EncryptedValue));
        Assert.Equal(events.OfType<ReasoningMessageStartEvent>().Single().MessageId, value.EntityId);
    }

    [Fact]
    public async Task A_value_in_the_same_content_as_its_text_follows_the_text()
    {
        var shaped = await ShapeAsync(ReasoningDisclosure.Full,
            Update(new TextReasoningContent("Checked the coil first.") { ProtectedData = "sig-1" }),
            Update(new TextContent("Clean the coil.")));

        var reasoning = shaped.SelectMany(u => u.Contents).OfType<TextReasoningContent>().ToList();
        Assert.Equal(("Checked the coil first.", (string?)null), (reasoning[0].Text, reasoning[0].ProtectedData));
        Assert.Equal("sig-1", reasoning[1].ProtectedData);
    }

    [Fact]
    public async Task An_update_that_mixes_reasoning_and_answer_is_split_so_rawEvent_cant_carry_the_reasoning()
    {
        var shaped = await ShapeAsync(ReasoningDisclosure.Hidden,
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Call 555-0141 if the coil is clean."), new TextContent("Clean the coil.")]));

        var answer = Assert.Single(shaped, u => u.Contents.OfType<TextContent>().Any());
        Assert.DoesNotContain(answer.Contents, c => c is TextReasoningContent);
    }

    [Fact]
    public async Task The_policy_asks_the_provider_for_no_more_than_it_will_show_without_changing_the_callers_options()
    {
        var inner = new ScriptedUpdates([Update(new TextContent("ok"))]);
        using var client = new ReasoningPolicyChatClient(inner, Options.Create(new ReasoningPolicy { Disclosure = ReasoningDisclosure.Summary }));
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } };

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Why?")], options, Runs.Ct))
        {
        }

        Assert.Equal((ReasoningEffort.Low, ReasoningOutput.Summary), (inner.Options!.Reasoning!.Effort, inner.Options.Reasoning.Output));
        Assert.Null(options.Reasoning.Output);
    }

    [Fact]
    public async Task Without_the_policy_AGUI_Server_names_a_message_it_never_opens_and_the_client_drops_the_value()
    {
        // A provider that shows no reasoning text, only its sealed value, straight into the SDK's converter.
        var events = await ConvertAsync(
            Update(new TextReasoningContent(null) { ProtectedData = "sealed-1" }),
            Update(new TextContent("Clean the coil.")));

        var value = events.OfType<ReasoningEncryptedValueEvent>().Single();
        Assert.DoesNotContain(events.OfType<ReasoningMessageStartEvent>(), s => s.MessageId == value.EntityId);

        // A client that follows the spec has nowhere to keep it: Beacon.Client.Core warns and drops it.
        using var conversation = new AgentConversation(new AGUIChatClient(new AGUIChatClientOptions { Transport = new Replay(events) }));
        await conversation.SendAsync("Why?", cancellationToken: Runs.Ct);

        Assert.DoesNotContain(conversation.View.Messages, m => m.EncryptedValue is not null);
        Assert.Contains(conversation.View.Warnings, w => w.Contains("encrypted value for unknown message", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_the_policy_a_value_in_the_same_content_as_its_text_arrives_before_its_message_opens()
    {
        var events = await ConvertAsync(Update(new TextReasoningContent("Checked the coil first.") { ProtectedData = "sig-1" }));

        var types = AGUIWire.Types(events);
        Assert.True(types.ToList().IndexOf("REASONING_ENCRYPTED_VALUE") < types.ToList().IndexOf("REASONING_MESSAGE_START"));
    }

    private static ChatResponseUpdate Update(AIContent content) =>
        new(ChatRole.Assistant, [content]) { ResponseId = "resp-1", MessageId = "msg-1" };

    private static async Task<List<ChatResponseUpdate>> ShapeAsync(ReasoningDisclosure disclosure, params ChatResponseUpdate[] updates)
    {
        using var client = new ReasoningPolicyChatClient(new ScriptedUpdates(updates), Options.Create(new ReasoningPolicy { Disclosure = disclosure }));
        List<ChatResponseUpdate> shaped = [];
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Why?")], cancellationToken: Runs.Ct))
        {
            shaped.Add(update);
        }

        return shaped;
    }

    // The SDK's converter on its own, as Chapter 4's endpoint uses it.
    private static async Task<List<BaseEvent>> ConvertAsync(params ChatResponseUpdate[] updates)
    {
        var json = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        json.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver);
        var context = Runs.Turn("t-1", "r-1", Runs.User("u-1", "Why?")).ToChatRequestContext(json);

        List<BaseEvent> events = [];
        await foreach (var evt in updates.ToAsyncEnumerable().AsAGUIEventStreamAsync(context, Runs.Ct))
        {
            events.Add(evt);
        }

        return events;
    }

    /// <summary>A model that returns fixed updates and remembers the options it was given.</summary>
    private sealed class ScriptedUpdates(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options = options;
            foreach (var update in updates)
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>A transport that plays recorded events, whatever the input.</summary>
    private sealed class Replay(IReadOnlyList<BaseEvent> events) : IAGUITransport
    {
        public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var evt in events)
            {
                await Task.Yield();
                yield return evt;
            }
        }
    }
}
