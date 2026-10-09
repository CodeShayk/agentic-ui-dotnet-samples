using Ch17.Media;
using Microsoft.Extensions.AI;

namespace Ch17.Field.Capture;

/// <summary>
/// The attachments waiting to go with Priya's next message. Every file is read and checked against
/// Beacon's media policy as it's added, so a file the agent would refuse never leaves the phone.
/// </summary>
public sealed class AttachmentTray(MediaPolicy policy)
{
    private readonly List<DataContent> _items = [];

    public event EventHandler? Changed;

    public IReadOnlyList<DataContent> Items => _items;

    public long PendingBytes => _items.Sum(i => (long)i.Data.Length);

    /// <summary>Reads and checks a file, and adds it when it passes.</summary>
    /// <returns>What happened, in words for the status line and the screen reader.</returns>
    public async Task<string> AddAsync(PickedFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (_items.Count >= policy.MaxPartsPerMessage)
        {
            return $"{MediaAttachment.SafeName(file.FileName)} wasn't added: a message can carry up to {policy.MaxPartsPerMessage} attachments.";
        }

        await using var stream = await file.OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = await MediaAttachment.ReadAsync(stream, file.FileName, file.Size, policy, cancellationToken).ConfigureAwait(false);
        if (result.Content is not { } content)
        {
            return result.Problem!;
        }

        _items.Add(content);
        Changed?.Invoke(this, EventArgs.Empty);
        return $"{content.Name} attached ({ByteSize.Format(content.Data.Length)}).";
    }

    public void Remove(DataContent item)
    {
        if (_items.Remove(item))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Builds the message to send and empties the tray, or explains why it can't: the agent refuses a
    /// request whose history carries more than the per-request limit, and every earlier attachment is in it.
    /// </summary>
    /// <param name="text">What the technician typed, if anything.</param>
    /// <param name="conversationBytes">The attachment bytes the conversation already carries.</param>
    public (ChatMessage? Message, string? Problem) TakeMessage(string? text, long conversationBytes)
    {
        if (string.IsNullOrWhiteSpace(text) && _items.Count == 0)
        {
            return (null, null);
        }

        if (conversationBytes + PendingBytes > policy.MaxBytesPerRequest)
        {
            return (null, $"This would bring the conversation to {ByteSize.Format(conversationBytes + PendingBytes)} of attachments; " +
                $"Beacon accepts {ByteSize.Format(policy.MaxBytesPerRequest)}. Start a new conversation to send it.");
        }

        List<AIContent> contents = [];
        if (!string.IsNullOrWhiteSpace(text))
        {
            contents.Add(new TextContent(text.Trim()));
        }

        contents.AddRange(_items);
        _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        return (new ChatMessage(ChatRole.User, contents), null);
    }
}
