using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Beacon.Client.Core;
using Ch08.Field.Core.Connection;
using Ch08.Field.Core.Recovery;

namespace Ch08.Field.Core.Presentation;

/// <summary>
/// The chat screen's state for native clients (Beacon Field on .NET MAUI, the WPF desktop client):
/// a transcript updated in place from Chapter 7's view state, a status line, commands, and what to do
/// after a turn that didn't finish. It never re-sends anything on its own.
/// </summary>
/// <remarks>
/// <see cref="AgentConversation.Changed"/> fires on whichever thread changed the view, usually a
/// thread-pool thread mid-stream. Each change asks the <see cref="IUiDispatcher"/> for one render, and a
/// render already queued absorbs the requests behind it, because it reads the latest view anyway.
/// </remarks>
public sealed class ChatViewModel : ObservableObject, IDisposable
{
    private readonly AgentConversation _conversation;
    private readonly IUiDispatcher _dispatcher;
    private readonly ChatViewModelOptions _options;
    private readonly TimeProvider _time;

    private int _renderQueued;
    private int _turnEnded;
    private ImmutableList<MessageView>? _renderedMessages;
    private ImmutableList<ToolCallView>? _renderedCalls;
    private int _runsBeforeTurn;
    private StopCause _stopCause = StopCause.User;
    private ITimer? _backgroundTimer;
    private bool _isBackgrounded;
    private string? _pendingAnnouncement;
    private string? _connectionNote;

    private string _draft = string.Empty;
    private string _statusText = "Ready";
    private bool _isBusy;
    private bool _isOnline = true;
    private RecoveryAdvice _recovery = RecoveryAdvice.None;

    /// <summary>Creates the view model over <paramref name="conversation"/>.</summary>
    /// <param name="conversation">The conversation to render and drive.</param>
    /// <param name="dispatcher">Posts work to the UI thread.</param>
    /// <param name="options">Tool safety, background behavior and the connection to watch.</param>
    public ChatViewModel(AgentConversation conversation, IUiDispatcher dispatcher, ChatViewModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _conversation = conversation;
        _dispatcher = dispatcher;
        _options = options ?? new ChatViewModelOptions();
        _time = _options.TimeProvider ?? TimeProvider.System;

        SendCommand = new RelayCommand(() => _ = SendAsync(Draft), CanSend);
        StopCommand = new RelayCommand(_conversation.Stop, () => IsBusy);
        NewChatCommand = new RelayCommand(NewChat, () => !IsBusy);
        RecoveryCommand = new RelayCommand(() => _ = SendAsync(Recovery.FollowUp!), () => !IsBusy && IsOnline && Recovery.FollowUp is not null);
        DismissRecoveryCommand = new RelayCommand(() => Recovery = RecoveryAdvice.None);

        _conversation.Changed += OnConversationChanged;
        if (_options.Transport is { } transport)
        {
            transport.ConnectionChanged += OnConnectionChanged;
        }
    }

    /// <summary>The transcript: user and assistant messages, each with its tool activity.</summary>
    public ObservableCollection<MessageItem> Messages { get; } = [];

    /// <summary>What the user is typing.</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            if (SetProperty(ref _draft, value ?? string.Empty))
            {
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>One line about what's happening, for the status bar.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>True while a turn is in progress.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>True while the device reports a network connection.</summary>
    public bool IsOnline
    {
        get => _isOnline;
        private set => SetProperty(ref _isOnline, value);
    }

    /// <summary>What to tell the user after a turn that didn't finish.</summary>
    public RecoveryAdvice Recovery
    {
        get => _recovery;
        private set
        {
            if (SetProperty(ref _recovery, value))
            {
                OnPropertyChanged(nameof(HasRecovery));
                OnPropertyChanged(nameof(HasRecoveryAction));
                RecoveryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True when there is recovery advice to show.</summary>
    public bool HasRecovery => _recovery.Kind != RecoveryKind.None;

    /// <summary>True when the advice offers an action.</summary>
    public bool HasRecoveryAction => _recovery is { ActionLabel: not null, FollowUp: not null };

    /// <summary>Sends <see cref="Draft"/>.</summary>
    public RelayCommand SendCommand { get; }

    /// <summary>Stops the turn in progress; what arrived is kept.</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>Starts a new conversation on a new thread.</summary>
    public RelayCommand NewChatCommand { get; }

    /// <summary>Runs the action the recovery advice offers: the question again, or a read-only check.</summary>
    public RelayCommand RecoveryCommand { get; }

    /// <summary>Hides the recovery advice.</summary>
    public RelayCommand DismissRecoveryCommand { get; }

    /// <summary>
    /// Raised on the UI thread with text a screen reader should speak: a finished answer, recovery advice or,
    /// once per turn, the first retry.
    /// </summary>
    public event EventHandler<string>? Announced;

    /// <summary>Sends a message as a new turn. Failures end the turn in the view; they don't throw.</summary>
    /// <param name="text">The message.</param>
    public async Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _conversation.View.IsBusy)
        {
            return;
        }

        if (_conversation.View.OpenInterrupts.Any())
        {
            StatusText = "Beacon is waiting for an answer to its question first.";
            return;
        }

        // A new turn: remember where it starts, so its runs and calls can be told apart afterwards.
        _runsBeforeTurn = _conversation.View.Runs.Count;
        _stopCause = StopCause.User;
        Recovery = RecoveryAdvice.None;
        if (ReferenceEquals(text, Draft) || text == Draft)
        {
            Draft = string.Empty;
        }

        await _conversation.SendAsync(text).ConfigureAwait(false);
    }

    /// <summary>The app went to the background. On platforms with a grace period, a turn still running when it ends is stopped.</summary>
    public void OnBackgrounded()
    {
        _isBackgrounded = true;
        if (_options.BackgroundGracePeriod is { } grace && _conversation.View.IsBusy)
        {
            _backgroundTimer?.Dispose();
            _backgroundTimer = _time.CreateTimer(_ => _dispatcher.Post(StopForBackground), null, grace, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// The app is in the foreground again. Nothing is re-sent: whatever happened meanwhile is already in
    /// the view, and the recovery advice says what to do about a turn that didn't finish.
    /// </summary>
    public void OnResumed()
    {
        _isBackgrounded = false;
        _backgroundTimer?.Dispose();
        _backgroundTimer = null;

        if (_pendingAnnouncement is { } announcement)
        {
            _pendingAnnouncement = null;
            Announced?.Invoke(this, announcement);
        }
    }

    /// <summary>The device's connectivity changed. Safe to call from any thread: the change is posted to the UI thread.</summary>
    /// <param name="online">True when the device reports internet access.</param>
    public void SetOnline(bool online) => _dispatcher.Post(() =>
    {
        IsOnline = online;
        Refresh(_conversation.View);
    });

    /// <summary>Stops listening to the conversation and the connection.</summary>
    public void Dispose()
    {
        _conversation.Changed -= OnConversationChanged;
        if (_options.Transport is { } transport)
        {
            transport.ConnectionChanged -= OnConnectionChanged;
        }

        _backgroundTimer?.Dispose();
    }

    // Any thread. One queued render at a time; it reads the latest view when it runs.
    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e)
    {
        if (e.Action is TurnEnded)
        {
            Volatile.Write(ref _turnEnded, 1);
        }

        if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
        {
            _dispatcher.Post(Render);
        }
    }

    // UI thread.
    private void Render()
    {
        Volatile.Write(ref _renderQueued, 0);
        var view = _conversation.View;

        // The reducer shares untouched slices, so a reference check says whether the transcript changed.
        if (!ReferenceEquals(view.Messages, _renderedMessages) || !ReferenceEquals(view.ToolCalls, _renderedCalls))
        {
            Project(view);
            (_renderedMessages, _renderedCalls) = (view.Messages, view.ToolCalls);
        }

        if (Interlocked.Exchange(ref _turnEnded, 0) == 1)
        {
            EndTurn(view);
        }

        Refresh(view);
    }

    private void EndTurn(ConversationView view)
    {
        _backgroundTimer?.Dispose();
        _backgroundTimer = null;
        _connectionNote = null;

        Recovery = TurnRecovery.Assess(view, _runsBeforeTurn, _options.Safety, _stopCause);

        var announcement = Recovery.Kind != RecoveryKind.None
            ? Recovery.Message
            : view.Status == ConversationStatus.Idle && view.Messages.OfType<AssistantMessageView>().LastOrDefault() is { Text.Length: > 0 } answer
                ? $"Beacon: {MarkdownText.ToPlainText(answer.Text)}"
                : null;

        if (announcement is null)
        {
            return;
        }

        if (_isBackgrounded)
        {
            _pendingAnnouncement = announcement; // nobody is looking; say it when they come back
        }
        else
        {
            Announced?.Invoke(this, announcement);
        }
    }

    private void Refresh(ConversationView view)
    {
        IsBusy = view.IsBusy;
        StatusText = view.Status switch
        {
            ConversationStatus.Starting => _connectionNote ?? "Sending…",
            ConversationStatus.Running => "Beacon is answering…",
            ConversationStatus.AwaitingInput => "Beacon is waiting for your answer.",
            ConversationStatus.Cancelled => "Beacon canceled the answer.",
            ConversationStatus.Stopped => "Stopped. What arrived is kept.",
            ConversationStatus.Failed => "The last answer didn't finish.",
            _ => IsOnline ? "Ready" : "Offline. Your draft is kept.",
        };

        SendCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        NewChatCommand.RaiseCanExecuteChanged();
        RecoveryCommand.RaiseCanExecuteChanged();
    }

    // Updates the transcript in place: same IDs keep their items, new ones are inserted, missing ones removed.
    private void Project(ConversationView view)
    {
        var index = 0;
        foreach (var message in view.Messages)
        {
            var (author, text, streaming, partial, activity) = message switch
            {
                UserMessageView user => (MessageAuthor.User, user.Text, false, false, (string?)null),
                AssistantMessageView assistant => (MessageAuthor.Assistant, assistant.Text, assistant.IsStreaming, assistant.IsPartial,
                    assistant.ToolCallIds.IsEmpty ? null : ToolActivity.Describe(view.ToolCallsOf(assistant))),
                _ => (MessageAuthor.Assistant, string.Empty, false, false, null), // reasoning, activity, tool: later chapters
            };

            if (text.Length == 0 && activity is null)
            {
                continue;
            }

            var item = ItemAt(index, message.Id, author);
            item.Update(text, streaming, partial, activity);
            index++;
        }

        while (Messages.Count > index)
        {
            Messages.RemoveAt(Messages.Count - 1);
        }
    }

    private MessageItem ItemAt(int index, string id, MessageAuthor author)
    {
        if (index < Messages.Count && Messages[index].Id == id)
        {
            return Messages[index];
        }

        for (var i = index + 1; i < Messages.Count; i++)
        {
            if (Messages[i].Id == id)
            {
                Messages.Move(i, index);
                return Messages[index];
            }
        }

        var item = new MessageItem(id, author);
        Messages.Insert(index, item);
        return item;
    }

    private void StopForBackground()
    {
        _backgroundTimer?.Dispose();
        _backgroundTimer = null;
        if (_isBackgrounded && _conversation.View.IsBusy)
        {
            // A clean stop now beats a socket the OS kills later: the agent sees the request end and
            // stops its model, and the client knows exactly what it received.
            _stopCause = StopCause.Background;
            _conversation.Stop();
        }
    }

    private void NewChat()
    {
        _conversation.Reset();
        Recovery = RecoveryAdvice.None;
    }

    private bool CanSend() =>
        !IsBusy && IsOnline && !string.IsNullOrWhiteSpace(Draft) && !_conversation.View.OpenInterrupts.Any();

    // Reading thread. Shows retries in the status line while the turn is starting.
    private void OnConnectionChanged(object? sender, ConnectionEvent e)
    {
        var reason = e.Error is HttpRequestException { StatusCode: not null } ? "Beacon is busy." : "No connection yet.";
        var note = e.Stage == ConnectionStage.Retrying
            ? $"{reason} Trying again (attempt {e.Attempt + 1} of {e.MaxAttempts})…"
            : null;

        _dispatcher.Post(() =>
        {
            _connectionNote = note;
            Refresh(_conversation.View);

            // A status line changes silently, so say the first retry once: a technician using a screen
            // reader learns why nothing has happened yet, without hearing every attempt.
            if (e is { Stage: ConnectionStage.Retrying, Attempt: 1 } && !_isBackgrounded)
            {
                Announced?.Invoke(this, $"{reason} Trying again.");
            }
        });
    }
}

/// <summary>Settings for <see cref="ChatViewModel"/>.</summary>
public sealed record ChatViewModelOptions
{
    /// <summary>Which tools only read; anything else is treated as having side effects.</summary>
    public ToolSafety Safety { get; init; } = ToolSafety.Beacon;

    /// <summary>
    /// How long a turn may keep running after the app goes to the background before it is stopped.
    /// <see langword="null"/> (desktop) never stops it; phones suspend background apps, so mobile sets one.
    /// </summary>
    public TimeSpan? BackgroundGracePeriod { get; init; }

    /// <summary>The retry layer, to show its progress in the status line.</summary>
    public ResilientAgentTransport? Transport { get; init; }

    /// <summary>The clock for the background grace period. Tests replace it.</summary>
    public TimeProvider? TimeProvider { get; init; }
}
