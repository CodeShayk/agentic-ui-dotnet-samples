using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ch15.Beacon.Agent.Input;

/// <summary>
/// Turns the model's requests for input into interrupts. It sits between FunctionInvokingChatClient and the
/// model, so it sees every call the model makes first. A call to one of Beacon's <see cref="InputForm"/>s
/// that needs a person's answer becomes an <see cref="InputRequestedContent"/>, recorded under a server-issued
/// ID; FunctionInvokingChatClient never sees it as a call, so the turn ends there and the run finishes with an
/// interrupt. Every other call passes through untouched.
/// </summary>
public sealed class InputRequestChatClient(
    IChatClient innerClient,
    InputForms forms,
    IInputRequestStore inputs,
    IOptions<InputOptions> options,
    TimeProvider time) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var run = InputRun.From(chatOptions);
        List<ChatResponseUpdate> held = [];

        await foreach (var update in base.GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false))
        {
            // Text streams through as it arrives. Calls are held until the response ends, because whether
            // they run depends on whether the model also asked a question.
            if (run is not null && update.Contents.Any(c => c is FunctionCallContent))
            {
                held.Add(update);
                continue;
            }

            yield return update;
        }

        var questions = held
            .SelectMany(u => u.Contents.OfType<FunctionCallContent>())
            .Select(call => (Call: call, Question: forms.Ask(call)))
            .Where(q => q.Question is not null)
            .ToList();

        if (questions.Count == 0)
        {
            foreach (var update in held)
            {
                yield return update;
            }

            yield break;
        }

        // A response that asks pauses the run, so its other calls are withheld: nothing should happen that the
        // person hasn't seen the question for. The model can make them again once it has the answers.
        foreach (var update in held)
        {
            if (update.Contents.Where(c => c is not FunctionCallContent).ToList() is { Count: > 0 } rest)
            {
                update.Contents = rest;
                yield return update;
            }
        }

        var expiresAt = WholeSeconds(time.GetUtcNow() + options.Value.TimeToLive);
        foreach (var (call, question) in questions)
        {
            var request = inputs.Propose(run!, call, question!, expiresAt);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new InputRequestedContent { InputId = request.Id, Request = request }])
            {
                ResponseId = held[0].ResponseId,
                MessageId = held[0].MessageId,
                ModelId = held[0].ModelId,
            };
        }
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ToChatResponseAsync(cancellationToken);

    // expiresAt goes on the wire to the second; the record must say exactly the same.
    private static DateTimeOffset WholeSeconds(DateTimeOffset at) => new(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, at.Offset);
}
