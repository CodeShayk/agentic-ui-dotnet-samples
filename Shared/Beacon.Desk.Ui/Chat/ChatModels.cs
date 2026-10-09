namespace Beacon.Desk.Ui;

/// <summary>Who wrote a message in the transcript.</summary>
public enum ChatAuthor
{
    User,
    Assistant,
}

/// <summary>
/// One message as the transcript shows it. This is a display model, not a protocol type: whatever
/// drives the chat (a page in Chapter 6, a reducer from Chapter 7) maps its own state onto it.
/// </summary>
/// <param name="Id">A stable ID, used as the render key. For assistant messages, the AG-UI message ID.</param>
/// <param name="Author">Who wrote it.</param>
/// <param name="Text">The text. Assistant text is Markdown and is rendered through <see cref="MarkdownView"/>.</param>
public sealed record TranscriptMessage(string Id, ChatAuthor Author, string Text)
{
    /// <summary>True when the run was stopped or failed before this message was finished.</summary>
    public bool IsPartial { get; init; }
}

/// <summary>What the chat is doing.</summary>
public enum ChatStatus
{
    /// <summary>Waiting for the user.</summary>
    Idle,

    /// <summary>A run is in progress.</summary>
    Streaming,

    /// <summary>The user stopped the last run.</summary>
    Stopped,

    /// <summary>The last run failed.</summary>
    Error,
}
