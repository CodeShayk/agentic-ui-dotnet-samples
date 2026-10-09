namespace Ch08.Field.Core.Presentation;

/// <summary>Who wrote a transcript entry.</summary>
public enum MessageAuthor
{
    /// <summary>The technician or operator.</summary>
    User,

    /// <summary>Beacon.</summary>
    Assistant,
}

/// <summary>
/// One entry in a native transcript, updated in place as its message streams, so a list control keeps
/// its scroll position and its items instead of rebuilding them on every delta.
/// </summary>
/// <param name="id">The AG-UI message ID.</param>
/// <param name="author">Who wrote it.</param>
public sealed class MessageItem(string id, MessageAuthor author) : ObservableObject
{
    private string _text = string.Empty;
    private IReadOnlyList<InlineRun> _runs = [];
    private bool _isStreaming;
    private bool _isPartial;
    private string? _activity;

    /// <summary>The AG-UI message ID.</summary>
    public string Id { get; } = id;

    /// <summary>Who wrote it.</summary>
    public MessageAuthor Author { get; } = author;

    /// <summary>True for the user's own messages.</summary>
    public bool IsUser => Author == MessageAuthor.User;

    /// <summary>The Markdown text so far. Untrusted.</summary>
    public string Text => _text;

    /// <summary>The text as styled runs for a native control (see <see cref="MarkdownText"/>).</summary>
    public IReadOnlyList<InlineRun> Runs => _runs;

    /// <summary>True while text is still arriving.</summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        private set => SetProperty(ref _isStreaming, value);
    }

    /// <summary>True when the turn ended before the message was finished.</summary>
    public bool IsPartial
    {
        get => _isPartial;
        private set => SetProperty(ref _isPartial, value);
    }

    /// <summary>What Beacon's tools are doing for this message, or <see langword="null"/>.</summary>
    public string? Activity
    {
        get => _activity;
        private set
        {
            if (SetProperty(ref _activity, value))
            {
                OnPropertyChanged(nameof(HasActivity));
            }
        }
    }

    /// <summary>True when <see cref="Activity"/> has something to show.</summary>
    public bool HasActivity => !string.IsNullOrEmpty(_activity);

    /// <summary>True when there is text to show (an assistant message can carry only tool calls).</summary>
    public bool HasText => _text.Length > 0;

    /// <summary>What a screen reader says for the entry.</summary>
    public string AccessibleText => $"{(IsUser ? "You" : "Beacon")}: {MarkdownText.ToPlainText(_text)}{(HasActivity ? " " + _activity : string.Empty)}";

    /// <summary>Applies the latest state of the message. Only what changed raises notifications.</summary>
    internal void Update(string text, bool isStreaming, bool isPartial, string? activity)
    {
        if (!string.Equals(_text, text, StringComparison.Ordinal))
        {
            _text = text;
            _runs = MarkdownText.ToRuns(text);
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(Runs));
            OnPropertyChanged(nameof(HasText));
            OnPropertyChanged(nameof(AccessibleText));
        }

        IsStreaming = isStreaming;
        IsPartial = isPartial;
        Activity = activity;
    }
}
