using System.Net;
using System.Text;
using AGUI.Abstractions;
using Beacon.Desk.Ui;
using Ch27.Beacon.Desk.Connection;
using Ch27.Beacon.Desk.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace Ch27.Beacon.Desk.Chat;

/// <summary>
/// Owns one conversation with Beacon's agent: the history it resends every turn, the ChatOptions that
/// keeps the AG-UI thread, the run in progress and what the panel shows. It talks to the agent only
/// through <see cref="IChatClient"/>, so the same code runs on the server or in the browser.
/// </summary>
public sealed partial class DeskChat : IDisposable
{
    // What we send: the whole conversation, every turn. The agent keeps nothing between runs.
    private readonly List<ChatMessage> _history = [];

    // One ChatOptions per conversation. AGUIChatClient pins the AG-UI thread ID onto it after the first run.
    private ChatOptions _options = new();

    // What we show.
    private readonly List<TranscriptMessage> _transcript = [];
    private readonly StringBuilder _streamText = new();
    private string? _streamId;
    private TranscriptMessage? _streamSnapshot;
    private ChatStatus _status = ChatStatus.Idle;
    private string? _threadId;
    private string? _runId;
    private string? _error;

    // The run in progress.
    private CancellationTokenSource? _run;
    private bool _stopRequested;
    private RenderThrottle _throttle = default!;
    private ChatPanel? _panel;

    [Inject] private IChatClient Agent { get; set; } = default!;

    [Inject] private ILogger<DeskChat> Logger { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    /// <summary>The shortest time between two renders while text streams. Zero renders every update.</summary>
    [Parameter] public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The longest a run may take before the Desk gives up on it.</summary>
    [Parameter] public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(2);

    protected override void OnInitialized() =>
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), RenderInterval);

    private async Task SendAsync(string text)
    {
        if (_run is not null || string.IsNullOrWhiteSpace(text))
        {
            return; // one run at a time
        }

        var question = new ChatMessage(ChatRole.User, text) { MessageId = $"user-{Guid.NewGuid():N}" };
        _history.Add(question);
        _transcript.Add(new TranscriptMessage(question.MessageId, ChatAuthor.User, text));
        var firstAnswerIndex = _transcript.Count;
        (_status, _error, _stopRequested) = (ChatStatus.Streaming, null, false);

        using var run = new CancellationTokenSource(RunTimeout);
        _run = run;
        List<ChatResponseUpdate> updates = [];
        string? runError = null;
        await _throttle.FlushAsync(); // show the question and the typing indicator at once

        try
        {
            await foreach (var update in Agent.GetStreamingResponseAsync(_history, _options, run.Token))
            {
                updates.Add(update);
                runError ??= Apply(update);
                await _throttle.RequestAsync();
            }

            (_status, _error) = runError is null ? (ChatStatus.Idle, null) : (ChatStatus.Error, runError);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            (_status, _error) = _stopRequested
                ? (ChatStatus.Stopped, null)
                : (ChatStatus.Error, $"Beacon didn't finish within {RunTimeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException ex)
        {
            // Not ours, so a timeout below the chat (Chapter 6): a ConnectTimeout the policy already retried.
            // It's a TaskCanceledException, not an HttpRequestException; letting it escape would end the circuit.
            Logger.LogWarning(ex, "Beacon's agent could not be reached in time.");
            (_status, _error) = (ChatStatus.Error, "Beacon's agent can't be reached. Check that it's running, then try again.");
        }
        catch (BrokenCircuitException ex)
        {
            // The policy's breaker is open: recent attempts couldn't connect, so this one wasn't sent.
            Logger.LogWarning(ex, "Beacon's agent is unreachable; the circuit breaker is open.");
            (_status, _error) = (ChatStatus.Error, "Beacon's agent can't be reached right now. Try again in a few seconds.");
        }
        catch (AgentRefusedException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // An answer the agent won't take, or one it already acted on, which must not look like one to send again.
            Logger.LogInformation("The agent refused the run: {Title} {Detail}", ex.Title, ex.Detail);
            (_status, _error) = (ChatStatus.Error, Refusal(ex, _history));
        }
        catch (SignInRequiredException ex)
        {
            // The sign-in cookie outlived the token cache (a restart or a new revision): sign in again, then come back.
            Logger.LogInformation(ex, "The user's token for the agent is gone; sending them to sign in again.");
            (_status, _error) = (ChatStatus.Error, "Your sign-in needs refreshing. Taking you to sign in again.");
            var here = "/" + Navigation.ToBaseRelativePath(Navigation.Uri);
            Navigation.NavigateTo($"{DeskAuthentication.SignInAgainPath}?returnUrl={Uri.EscapeDataString(here)}", forceLoad: true);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            (_status, _error) = (ChatStatus.Error, "You've asked Beacon a lot in the last minute. Wait a moment, then try again.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Logger.LogWarning(ex, "Beacon's agent refused the user's token.");
            (_status, _error) = (ChatStatus.Error, "Beacon's agent didn't accept your sign-in. Reload the page to sign in again.");
        }
        catch (AgentRefusedException ex) when (ex.Title is { Length: > 0 })
        {
            // The agent said why (a run still in progress, say), so the dispatcher hears that, not "can't be reached".
            Logger.LogWarning(ex, "Beacon's agent refused the run: {Detail}", ex.Detail);
            (_status, _error) = (ChatStatus.Error, $"Beacon couldn't take this request: {ex.Title.TrimEnd('.')}. Try again in a moment.");
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Beacon's agent could not be reached.");
            (_status, _error) = (ChatStatus.Error, "Beacon's agent can't be reached. Check that it's running, then try again.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An exception escaping an event handler would end a Blazor Server circuit. Show it instead.
            Logger.LogError(ex, "The run failed.");
            (_status, _error) = (ChatStatus.Error, "The answer stopped unexpectedly. Try again.");
        }
        finally
        {
            _run = null;
        }

        Finish(completed: _status == ChatStatus.Idle, updates, firstAnswerIndex);
        await _throttle.FlushAsync();

        if (_status == ChatStatus.Stopped && _panel is not null)
        {
            await _panel.FocusComposerAsync(); // the Stop button just became disabled; don't strand focus on it
        }
    }

    /// <summary>
    /// What Dana reads when the agent refuses a run with a 409, worded as Chapter 14's approval dialog words it. "Interrupt
    /// already answered" covers an approval whose call ran and a denial the model already read; only an approval can
    /// have ordered anything, so the answer the run carried decides the wording. New chat is the way on.
    /// </summary>
    public static string Refusal(AgentRefusedException refusal, IReadOnlyList<ChatMessage> sent)
    {
        var answer = sent.LastOrDefault()?.Contents.OfType<ToolApprovalResponseContent>().LastOrDefault();
        return (refusal.Title, answer) switch
        {
            ("Interrupt already answered", { Approved: false }) =>
                "Beacon already has this answer from an earlier run, and nothing was carried out. Start a new chat to go on.",
            ("Interrupt already answered", _) =>
                "Beacon already acted on this approval in an earlier run. Check the order in a new chat before asking again.",
            (_, not null) =>
                "Beacon didn't accept that answer. The request may have expired at the agent or been answered elsewhere, "
                + "or the agent restarted. Start a new chat to go on.",
            _ => $"Beacon couldn't take this request: {(refusal.Title ?? "it conflicts with another run").TrimEnd('.')}. Try again in a moment.",
        };
    }

    // Folds one update into what the panel shows. Returns the run's error message, if it reported one.
    private string? Apply(ChatResponseUpdate update)
    {
        if (update.RawRepresentation is RunStartedEvent started)
        {
            (_threadId, _runId) = (started.ThreadId, started.RunId);
        }

        string? error = null;
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent { Text.Length: > 0 } text when update.Role == ChatRole.Assistant:
                    AppendAssistantText(update.MessageId ?? update.ResponseId ?? "assistant", text.Text);
                    break;

                case ErrorContent failure: // RUN_ERROR
                    error = failure.Message;
                    break;
            }
        }

        return error;
    }

    private void AppendAssistantText(string messageId, string delta)
    {
        if (_streamId != messageId)
        {
            CommitStreamingMessage(partial: false); // a new message began, so the previous one is finished
            _streamId = messageId;
        }

        _streamText.Append(delta);
    }

    // The streamed text as a display message. The string is built only when a render asks for it,
    // so a burst of updates between two renders costs one allocation, not one per update.
    private TranscriptMessage? StreamingMessage
    {
        get
        {
            if (_streamId is null)
            {
                return null;
            }

            if (_streamSnapshot is null || _streamSnapshot.Text.Length != _streamText.Length)
            {
                _streamSnapshot = new TranscriptMessage(_streamId, ChatAuthor.Assistant, _streamText.ToString());
            }

            return _streamSnapshot;
        }
    }

    private void CommitStreamingMessage(bool partial)
    {
        if (_streamId is not null && _streamText.Length > 0)
        {
            _transcript.Add(new TranscriptMessage(_streamId, ChatAuthor.Assistant, _streamText.ToString()) { IsPartial = partial });
        }

        (_streamId, _streamSnapshot) = (null, null);
        _streamText.Clear();
    }

    // Moves the run's output into the transcript and into the history the next run sends.
    private void Finish(bool completed, List<ChatResponseUpdate> updates, int firstAnswerIndex)
    {
        CommitStreamingMessage(partial: !completed);

        if (completed)
        {
            _history.AddMessages(updates); // text, tool calls and tool results, as the agent sent them
            return;
        }

        // A stopped or failed run can end between a tool call and its result, and most models reject
        // a history like that. Keep only what the user saw: the text.
        foreach (var shown in _transcript.Skip(firstAnswerIndex))
        {
            _history.Add(new ChatMessage(ChatRole.Assistant, shown.Text) { MessageId = shown.Id });
        }
    }

    private void Stop()
    {
        if (_run is { IsCancellationRequested: false } run)
        {
            _stopRequested = true;
            run.Cancel(); // aborts the HTTP request; the agent sees RequestAborted and stops the model
        }
    }

    private void NewChat()
    {
        if (_run is not null)
        {
            return;
        }

        _history.Clear();
        _transcript.Clear();
        _options = new ChatOptions(); // a fresh ChatOptions means a fresh AG-UI thread
        (_status, _threadId, _runId, _error) = (ChatStatus.Idle, null, null, null);
    }

    public void Dispose()
    {
        _run?.Cancel(); // leaving the page stops the run, and with it the agent's work
        _throttle.Dispose();
    }
}
