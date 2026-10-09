using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beacon.Client.Core;

/// <summary>
/// One conversation with an AG-UI agent: it keeps the view state, sends each turn through an
/// <see cref="IChatClient"/> (normally <c>AGUIChatClient</c>), folds every update into the view and
/// tells the UI when the view changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> Every change goes through <see cref="Dispatch"/>, which reduces under a lock, so
/// changes are applied one at a time in the order they happen. <see cref="View"/> is an immutable
/// snapshot that is safe to read from any thread. <see cref="Changed"/> is raised after each change on
/// whichever thread made it, usually a thread-pool thread while a turn streams: a UI marshals to its
/// own thread (<c>InvokeAsync</c> in Blazor, <c>MainThread</c> in .NET MAUI) and renders
/// <see cref="View"/>, which is always the latest.
/// </para>
/// <para>
/// <b>Turns.</b> One turn at a time: <see cref="SendAsync(string, RunInputs?, CancellationToken)"/>,
/// <see cref="RespondAsync"/> and <see cref="ContinueAsync"/> each run one, and complete when it
/// ends. They don't throw for failures the agent or the network cause (a refusal, a timeout, a broken
/// stream, <c>RUN_ERROR</c>, a protocol violation); those end the turn with
/// <see cref="ConversationStatus.Failed"/> and a <see cref="RunFailure"/>. Misuse does throw: starting a
/// turn while one runs or past an open interrupt, answering an interrupt that isn't open or has
/// expired, or using a disposed conversation.
/// </para>
/// <para>
/// <b>Interrupts.</b> An answer (or an abandonment) counts only once the agent accepts it, which is when
/// the resuming run's <c>RUN_STARTED</c> arrives. If the agent refuses it first, the interrupt stays open
/// with <see cref="InterruptView.LastAnswerFailure"/> set, so the user can answer again or abandon it.
/// </para>
/// </remarks>
public sealed class AgentConversation : IDisposable
{
    private readonly IChatClient _client;
    private readonly ConversationOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private ConversationView _view = ConversationView.Empty;
    private ChatOptions _chatOptions = new();
    private CancellationTokenSource? _turn;
    private RunInputs? _turnInputs;
    private List<string>? _pendingAnswers;
    private bool _stopRequested;
    private bool _violation;
    private IDisposable? _tapSubscription;
    private bool _disposed;

    /// <summary>Creates a conversation over <paramref name="client"/>.</summary>
    /// <param name="client">The chat client, normally <c>AGUIChatClient</c>. It must include its own function invocation for frontend tools.</param>
    /// <param name="options">Settings; <see langword="null"/> uses the defaults.</param>
    public AgentConversation(IChatClient client, ConversationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _options = options ?? new ConversationOptions();
        _logger = _options.Logger ?? NullLogger.Instance;
        _time = _options.TimeProvider ?? TimeProvider.System;
        Reset(_options.ThreadId);
    }

    /// <summary>Raised after every change to <see cref="View"/>. See the remarks on <see cref="AgentConversation"/> for threading.</summary>
    public event EventHandler<ConversationChangedEventArgs>? Changed;

    /// <summary>The current view. Immutable; read it whenever you render.</summary>
    public ConversationView View => Volatile.Read(ref _view);

    /// <summary>Sends a user message and streams the agent's answer into the view.</summary>
    /// <param name="text">What the user typed.</param>
    /// <param name="inputs">Extra <c>RunAgentInput</c> fields for this turn, if any.</param>
    /// <param name="cancellationToken">Cancels the turn, like <see cref="Stop"/>.</param>
    /// <exception cref="InvalidOperationException">A turn is in progress, or an interrupt is open and <paramref name="inputs"/> doesn't resolve it.</exception>
    public Task SendAsync(string text, RunInputs? inputs = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return SendAsync(new ChatMessage(ChatRole.User, text), inputs, cancellationToken);
    }

    /// <summary>Sends a user message (text, attachments or both) and streams the agent's answer into the view.</summary>
    /// <param name="message">A user message. If its <see cref="ChatMessage.MessageId"/> is unset, one is assigned.</param>
    /// <param name="inputs">Extra <c>RunAgentInput</c> fields for this turn, if any.</param>
    /// <param name="cancellationToken">Cancels the turn, like <see cref="Stop"/>.</param>
    /// <exception cref="InvalidOperationException">A turn is in progress, or an interrupt is open and <paramref name="inputs"/> doesn't resolve it.</exception>
    public async Task SendAsync(ChatMessage message, RunInputs? inputs = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Role != ChatRole.User)
        {
            throw new ArgumentException("Only user messages can be sent; the agent owns the other roles.", nameof(message));
        }

        // AG-UI requires an ID on every message, and AGUIChatClient sends an unset one as "".
        message.MessageId ??= NewId("user");

        var turn = BeginTurn(inputs, answered: [], cancellationToken);
        Dispatch(new MessageSent(message));
        await RunTurnAsync(turn, ConversationHistory.ToChatMessages(View), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers the open interrupts and resumes the agent: approvals with <see cref="ToolApprovalResponseContent"/>
    /// (from <see cref="ToolApprovalRequestContent.CreateResponse"/>), input with <c>InterruptResponseContent</c>.
    /// </summary>
    /// <param name="responses">One answer per open interrupt, each carrying the interrupt's ID as its request ID.</param>
    /// <param name="inputs">Extra <c>RunAgentInput</c> fields for this turn, if any. <see cref="RunInputs.Resume"/> isn't allowed here.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <exception cref="ArgumentException">An answer doesn't match an open interrupt, or answers one that has expired.</exception>
    /// <exception cref="InvalidOperationException">A turn is in progress, or an open interrupt has no answer.</exception>
    /// <remarks>
    /// The history sent ends with the requests (as an assistant message) and the answers (as a user
    /// message), the shape <c>AGUIChatClient</c> turns into <c>RunAgentInput.resume</c>. To abandon an
    /// interrupt instead, write the whole resume list and call <see cref="ContinueAsync"/>:
    /// <c>AGUIChatClient</c> ignores response content when the caller supplies a resume list.
    /// </remarks>
    public async Task RespondAsync(IEnumerable<InputResponseContent> responses, RunInputs? inputs = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(responses);
        List<InputResponseContent> answers = [.. responses];
        if (answers.Count == 0)
        {
            throw new ArgumentException("Pass at least one response.", nameof(responses));
        }

        if (inputs?.Resume is { Count: > 0 })
        {
            throw new ArgumentException("Answer with responses or with a resume list (ContinueAsync), not both.", nameof(inputs));
        }

        var view = View;
        List<AIContent> requests = [];
        foreach (var answer in answers)
        {
            var interrupt = view.OpenInterrupts.FirstOrDefault(i => i.Id == answer.RequestId)
                ?? throw new ArgumentException($"No open interrupt has the ID '{answer.RequestId}'.", nameof(responses));
            if (IsExpired(interrupt))
            {
                throw new ArgumentException($"Interrupt '{interrupt.Id}' expired at {interrupt.ExpiresAt}; it can only be abandoned.", nameof(responses));
            }

            requests.Add(interrupt.Request ?? RequestFor(interrupt, view));
        }

        var turn = BeginTurn(inputs, [.. answers.Select(a => a.RequestId)], cancellationToken);
        var history = ConversationHistory.ToChatMessages(View);
        history.Add(new ChatMessage(ChatRole.Assistant, requests) { MessageId = NewId("interrupts") });
        history.Add(new ChatMessage(ChatRole.User, [.. answers]) { MessageId = NewId("user") });

        await RunTurnAsync(turn, history, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the agent again on the current history, with no new user message: to resume with a
    /// resume list written by hand (for example, abandoning an interrupt), to branch with
    /// <see cref="RunInputs.ParentRunId"/> or to continue after the client added a tool result itself.
    /// </summary>
    /// <param name="inputs">Extra <c>RunAgentInput</c> fields for this turn, if any.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <exception cref="InvalidOperationException">A turn is in progress, or an interrupt is open and <paramref name="inputs"/> doesn't resolve it.</exception>
    public async Task ContinueAsync(RunInputs? inputs = null, CancellationToken cancellationToken = default)
    {
        var turn = BeginTurn(inputs, answered: [], cancellationToken);
        await RunTurnAsync(turn, ConversationHistory.ToChatMessages(View), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the current turn. The request is aborted, so the agent sees it and stops its model call; what arrived is kept.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_turn is { IsCancellationRequested: false } turn)
            {
                _stopRequested = true;
                turn.Cancel();
            }
        }
    }

    /// <summary>Starts a new conversation on a new thread: an empty view and a fresh <see cref="ChatOptions"/>.</summary>
    /// <param name="threadId">The new thread's ID; <see langword="null"/> mints one.</param>
    /// <exception cref="InvalidOperationException">A turn is in progress; stop it first.</exception>
    public void Reset(string? threadId = null)
    {
        threadId ??= NewId("thread");

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_turn is not null)
            {
                throw new InvalidOperationException("Stop the current turn before starting a new conversation.");
            }

            // One ChatOptions per thread, reused for every run. The factory runs once per run and
            // builds the RunAgentInput fields AGUIChatClient can't derive from messages.
            _chatOptions = new ChatOptions
            {
                Tools = _options.Tools.Count > 0 ? [.. _options.Tools] : null,
                RawRepresentationFactory = _ => CreateRunInput(),
            };

            _tapSubscription?.Dispose();
            _tapSubscription = _options.Tap?.Observe(threadId, evt => Dispatch(new WireEventObserved(evt)));
        }

        Dispatch(new ConversationReset(threadId));
    }

    /// <summary>Replaces the shared state with the client's own edit. The next run sends it to the agent.</summary>
    /// <param name="document">The new state. It is copied, so later changes to it don't leak into the view.</param>
    public void UpdateState(JsonNode? document) => Dispatch(new StateEdited(document?.DeepClone()));

    /// <summary>
    /// Applies an action to the view and raises <see cref="Changed"/>. Every change goes through here,
    /// including the ones the conversation makes itself, so actions are applied one at a time.
    /// </summary>
    /// <param name="action">What happened.</param>
    public void Dispatch(ConversationAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Apply(action, endsTurn: false);
    }

    private void Apply(ConversationAction action, bool endsTurn)
    {
        ConversationView next;
        bool changed;
        CancellationTokenSource? abort = null;
        lock (_gate)
        {
            if (endsTurn)
            {
                // Release the turn and record how it ended in one step, so a handler of this Changed
                // can start the next turn, and no other turn can start in between.
                (_turn, _turnInputs, _pendingAnswers, _stopRequested, _violation) = (null, null, null, false, false);
            }
            else if (_violation && action is UpdateReceived or WireEventObserved)
            {
                return; // the turn is being cancelled; nothing more from its stream counts
            }

            var previous = _view;
            next = ConversationReducer.Reduce(previous, action);
            changed = !ReferenceEquals(next, previous);
            if (changed)
            {
                Volatile.Write(ref _view, next);
            }

            // The reducer found a malformed value: the run has failed, so the request is aborted too.
            if (!endsTurn && _turn is { } turn && next.Failure is { Kind: FailureKind.ProtocolViolation } failure
                && !ReferenceEquals(failure, previous.Failure))
            {
                _violation = true;
                abort = turn;
            }
        }

        if (abort is not null)
        {
            _logger.LogWarning("The agent's stream broke an AG-UI rule, so the turn was cancelled: {Reason}", next.Failure?.Message);
            abort.Cancel();
        }

        // Raised outside the lock: a handler may dispatch, and it may block on a UI thread.
        if (changed)
        {
            Changed?.Invoke(this, new ConversationChangedEventArgs(next, action));
        }
    }

    /// <summary>Stops any turn in progress and stops observing the tap.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_turn is { IsCancellationRequested: false } turn)
            {
                _stopRequested = true; // leaving the page stops the run, and with it the agent's work
                turn.Cancel();
            }

            _tapSubscription?.Dispose();
        }
    }

    // One turn at a time, and never past an open interrupt. The checks and the claim happen under the
    // lock, so two quick clicks can't both start a turn. The interrupt rules are AG-UI's, and they bind
    // the client: the resume list must cover every interrupt of the interrupted run (omission is not
    // abandonment), an expired interrupt can only be abandoned, and both are checked before anything
    // is sent. The answers are only recorded as pending: they count once the resuming run starts.
    private CancellationTokenSource BeginTurn(RunInputs? inputs, IReadOnlyList<string> answered, CancellationToken cancellationToken)
    {
        CancellationTokenSource turn;
        List<string> resolved = [.. answered];

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_turn is not null)
            {
                throw new InvalidOperationException("A turn is already in progress.");
            }

            foreach (var entry in inputs?.Resume ?? [])
            {
                if (entry.Status == ResumeStatus.Resolved && _view.FindInterrupt(entry.InterruptId) is { } interrupt && IsExpired(interrupt))
                {
                    throw new InvalidOperationException($"Interrupt '{entry.InterruptId}' expired at {interrupt.ExpiresAt}; it can only be abandoned.");
                }

                resolved.Add(entry.InterruptId);
            }

            var uncovered = _view.OpenInterrupts.Select(i => i.Id).Except(resolved).ToList();
            if (uncovered.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The agent is waiting on interrupt(s) {string.Join(", ", uncovered)}. Answer them (RespondAsync) or abandon them in a resume list before it can continue.");
            }

            turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            turn.CancelAfter(_options.TurnTimeout);
            (_turn, _turnInputs, _pendingAnswers, _stopRequested, _violation) = (turn, inputs, resolved.Count > 0 ? resolved : null, false, false);
        }

        return turn;
    }

    // The resuming run started, so the agent took the answers: each interrupt is answered once, here.
    private void AcceptAnswers()
    {
        List<string>? accepted;
        lock (_gate)
        {
            (accepted, _pendingAnswers) = (_pendingAnswers, null);
        }

        if (accepted is { Count: > 0 })
        {
            Dispatch(new InterruptsAnswered(accepted));
        }
    }

    private bool IsExpired(InterruptView interrupt) =>
        DateTimeOffset.TryParse(interrupt.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAt)
        && expiresAt <= _time.GetUtcNow();

    private async Task RunTurnAsync(CancellationTokenSource turn, List<ChatMessage> history, CancellationToken cancellationToken)
    {
        TurnEnded ending;
        var received = false;
        try
        {
            Dispatch(new TurnStarted());

            // Streaming, never GetResponseAsync: lifecycle, state and activity arrive on updates with
            // no content, and aggregating them into a ChatResponse drops them.
            await foreach (var update in _client.GetStreamingResponseAsync(history, _chatOptions, turn.Token).ConfigureAwait(false))
            {
                received = true;
                if (update.RawRepresentation is RunStartedEvent)
                {
                    AcceptAnswers();
                }

                Dispatch(new UpdateReceived(update));
                turn.Token.ThrowIfCancellationRequested(); // Stop, or a violation, ends the turn here
            }

            ending = new TurnEnded(TurnEnd.Completed);
        }
        catch (Exception) when (Volatile.Read(ref _violation))
        {
            // The reducer found a malformed value and the request was aborted; however the abort
            // surfaced, the turn failed with that protocol violation.
            ending = new TurnEnded(TurnEnd.Failed) { Failure = View.Failure };
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested)
        {
            // Ours: Stop, the caller's token or TurnTimeout.
            ending = Volatile.Read(ref _stopRequested) || cancellationToken.IsCancellationRequested
                ? new TurnEnded(TurnEnd.Stopped)
                : Failed(new(FailureKind.TimedOut, $"The agent didn't finish within {_options.TurnTimeout.TotalSeconds:0} seconds."));
        }
        catch (OperationCanceledException ex)
        {
            // Not ours: HttpClient.Timeout or SocketsHttpHandler.ConnectTimeout expired. Both are
            // cancellations, never HttpRequestException, and only the connect timeout proves the
            // request never reached the agent.
            _logger.LogWarning(ex, "A request to the agent timed out.");
            ending = Failed(TimeoutFailure(ex, received));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "The agent refused the request or could not be reached.");
            ending = Failed(HttpFailure(ex));
        }
        catch (IOException ex)
        {
            // The connection broke mid-stream (a reset, or a response that ended early): a truncated run.
            _logger.LogWarning(ex, "The connection to the agent broke mid-stream.");
            ending = Failed(new(FailureKind.StreamEnded, "The connection to the agent broke before the run finished."));
        }
        catch (InvalidOperationException ex)
        {
            // AGUIChatClient checks the stream as it reads and throws on an ordering violation.
            _logger.LogWarning(ex, "The agent's stream broke an AG-UI rule.");
            ending = Failed(new(FailureKind.ProtocolViolation, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A turn failed unexpectedly.");
            ending = Failed(new(FailureKind.Unexpected, "The answer stopped unexpectedly."));
        }

        // Answers whose resuming run never started weren't accepted; they stay open.
        List<string>? unaccepted;
        lock (_gate)
        {
            (unaccepted, _pendingAnswers) = (_pendingAnswers, null);
        }

        Apply(unaccepted is { Count: > 0 } ? ending with { UnacceptedAnswers = unaccepted } : ending, endsTurn: true);
        turn.Dispose();
    }

    // SocketsHttpHandler.ConnectTimeout expires as an OperationCanceledException around a TimeoutException
    // with no inner exception; HttpClient.Timeout's TimeoutException wraps the operation it cancelled
    // (verified against .NET 11 RC1). Anything else is treated as "may be running".
    private static RunFailure TimeoutFailure(OperationCanceledException exception, bool received) =>
        exception.InnerException is TimeoutException { InnerException: not (OperationCanceledException or HttpRequestException or IOException) }
            ? new(FailureKind.TimedOut, "Beacon couldn't connect to the agent in time.") { RunNeverStarted = true }
            : new(FailureKind.TimedOut, received ? "The agent's stream timed out before the run finished." : "The agent didn't start answering in time.");

    // A status means the agent, or a gateway in front of it, answered instead of streaming. A 4xx is a
    // refusal and the run never started. A 5xx is the server's failure; a 502 or 504 came from a gateway
    // that may have forwarded the request. With no status, only a failure to connect proves the run
    // never started.
    private static RunFailure HttpFailure(HttpRequestException exception)
    {
        if (exception.StatusCode is { } status)
        {
            var code = (int)status;
            return code is >= 400 and < 500
                ? new(FailureKind.Rejected, $"The agent refused the request (HTTP {code}).") { StatusCode = status, RunNeverStarted = true }
                : new(FailureKind.Unreachable, $"The agent answered with HTTP {code} instead of a stream.")
                {
                    StatusCode = status,
                    RunNeverStarted = status is not (HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout),
                };
        }

        return new(FailureKind.Unreachable, "The agent can't be reached.")
        {
            RunNeverStarted = exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError,
        };
    }

    // Called by AGUIChatClient once per run, through ChatOptions.RawRepresentationFactory. It takes the
    // thread ID from here (the run ID too, but we leave that for it to mint), copies State, ParentRunId,
    // Context, ForwardedProperties and Resume when they are set, and uses Messages only if not empty.
    private RunAgentInput CreateRunInput()
    {
        RunInputs? once;
        lock (_gate)
        {
            (once, _turnInputs) = (_turnInputs, null); // the turn's extras go on its first run only
        }

        var view = View;
        var input = new RunAgentInput
        {
            ThreadId = view.ThreadId ?? throw new InvalidOperationException("The conversation has no thread."),
            State = once?.State ?? (_options.SendState ? view.State.ToJsonElement() : null),
            ParentRunId = once?.ParentRunId,
            Context = once?.Context,
            ForwardedProperties = once?.ForwardedProperties,
            Resume = once?.Resume,
        };

        if (once?.Messages is { Count: > 0 } messages)
        {
            input.Messages = messages;
        }

        _options.ConfigureInput?.Invoke(input, view);

        // The run's deltas apply to the state this input carries; the view adopts it if it differs.
        Dispatch(new RunInputSent(input));
        return input;
    }

    // An interrupt that arrived only on RUN_FINISHED has no request content; build the one
    // AGUIChatClient would have made.
    private static InputRequestContent RequestFor(InterruptView interrupt, ConversationView view) =>
        interrupt.Kind == InterruptKind.ToolApproval && interrupt.ToolCallId is { } callId && view.FindToolCall(callId) is { } call
            ? new ToolApprovalRequestContent(interrupt.Id, new FunctionCallContent(call.Id, call.Name, JsonValues.ToArguments(call.Arguments)))
            : new InterruptRequestContent(interrupt.Id)
            {
                Reason = interrupt.Reason,
                Message = interrupt.Message,
                ToolCallId = interrupt.ToolCallId,
                ResponseSchema = interrupt.ResponseSchema,
                ExpiresAt = interrupt.ExpiresAt,
                Metadata = interrupt.Metadata,
            };

    private static TurnEnded Failed(RunFailure failure) => new(TurnEnd.Failed) { Failure = failure };

    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}
