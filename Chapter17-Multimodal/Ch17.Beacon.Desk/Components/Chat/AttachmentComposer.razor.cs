using Ch17.Media;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.AI;
using Microsoft.JSInterop;

namespace Ch17.Beacon.Desk.Components.Chat;

/// <summary>An attachment waiting to be sent, with its preview.</summary>
public sealed record PendingAttachment(string Id, DataContent Content, MediaRule Rule, string? PreviewUrl)
{
    public string Name => Content.Name ?? "attachment";

    public long Size => Content.Data.Length;
}

/// <summary>
/// The Desk's composer with attachments. Files are checked here with the agent's own rules, so Dana
/// learns at once that a file won't be accepted, and nothing is uploaded that the endpoint would refuse.
/// </summary>
public sealed partial class AttachmentComposer
{
    /// <summary>Photos larger than this are resized in the browser before they're read.</summary>
    public const long ResizeAbove = 2 * ByteSize.MB;

    private readonly string _fileId = $"ch17-file-{Guid.NewGuid():N}";
    private readonly string _limitsId = $"ch17-limits-{Guid.NewGuid():N}";
    private readonly string _inputId = $"ch17-input-{Guid.NewGuid():N}";
    private readonly List<PendingAttachment> _pending = [];
    private readonly List<string> _problems = [];
    private ElementReference _input;
    private string _draft = string.Empty;
    private string _announcement = string.Empty;
    private bool _reading;

    [Inject] private MediaPolicy Policy { get; set; } = default!;

    /// <summary>The attachment bytes the conversation already carries; every later turn sends them again.</summary>
    [Parameter] public long ConversationBytes { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public bool Busy { get; set; }

    /// <summary>Raised with the user message to send: its text, then its attachments.</summary>
    [Parameter] public EventCallback<ChatMessage> OnSend { get; set; }

    [Parameter] public EventCallback OnStop { get; set; }

    private long PendingBytes => _pending.Sum(p => p.Size);

    public ValueTask FocusAsync() => _input.Context is null || Disabled ? ValueTask.CompletedTask : _input.FocusAsync();

    private async Task OnFilesChosenAsync(InputFileChangeEventArgs e)
    {
        _problems.Clear();
        _reading = true;
        List<string> added = [];

        try
        {
            // GetMultipleFiles throws when asked for fewer files than were chosen, so ask for all of them.
            foreach (var file in e.GetMultipleFiles(e.FileCount))
            {
                if (_pending.Count >= Policy.MaxPartsPerMessage)
                {
                    _problems.Add($"{file.Name} wasn't added: a message can carry up to {Policy.MaxPartsPerMessage} attachments.");
                    continue;
                }

                if (await ReadAsync(file) is { } attachment)
                {
                    _pending.Add(attachment);
                    added.Add($"{attachment.Name} attached, {ByteSize.Format(attachment.Size)}.");
                }
            }
        }
        finally
        {
            _reading = false;
        }

        _announcement = string.Join(' ', added);
    }

    private async Task<PendingAttachment?> ReadAsync(IBrowserFile file)
    {
        try
        {
            // A large photo is resized in the browser first: a 12-megapixel JPEG becomes a few hundred
            // kilobytes. MediaAttachment.ReadAsync strips the metadata from every photo, resized or not.
            var source = file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && file.Size > ResizeAbove
                ? await file.RequestImageFileAsync("image/jpeg", 2048, 2048)
                : file;

            if (source.Size > Policy.LargestFile)
            {
                _problems.Add($"{file.Name} is {ByteSize.Format(source.Size)}. Beacon accepts {Policy.Describe()}.");
                return null;
            }

            // The bytes decide the type: file.ContentType is only the browser's guess from the extension.
            await using var stream = source.OpenReadStream(maxAllowedSize: Policy.LargestFile);
            var result = await MediaAttachment.ReadAsync(stream, file.Name, source.Size, Policy);
            if (result.Content is not { } content)
            {
                _problems.Add(result.Problem!);
                return null;
            }

            var rule = Policy.Find(content.MediaType)!;
            var preview = rule.Kind == MediaKind.Image ? await PreviewAsync(file) : null;
            return new PendingAttachment(Guid.NewGuid().ToString("N"), content, rule, preview);
        }
        catch (IOException)
        {
            _problems.Add($"{file.Name} couldn't be read. Try choosing it again.");
            return null;
        }
    }

    // A small thumbnail made by the browser, so the preview costs a few kilobytes, not the whole photo.
    private static async Task<string?> PreviewAsync(IBrowserFile file)
    {
        try
        {
            var thumbnail = await file.RequestImageFileAsync("image/jpeg", 160, 160);
            await using var stream = thumbnail.OpenReadStream(maxAllowedSize: 256 * ByteSize.KB);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            return $"data:image/jpeg;base64,{Convert.ToBase64String(buffer.ToArray())}";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JSException)
        {
            return null; // no preview is fine; the file is still attached
        }
    }

    private void Remove(PendingAttachment item)
    {
        _pending.Remove(item);
        _announcement = $"{item.Name} removed.";
    }

    private async Task SubmitAsync()
    {
        if (Disabled || Busy || _reading || (string.IsNullOrWhiteSpace(_draft) && _pending.Count == 0))
        {
            return;
        }

        // The agent refuses a request whose history carries more than the per-request limit, and a
        // refused message would stay in the conversation and be resent. So the Desk checks first.
        if (ConversationBytes + PendingBytes > Policy.MaxBytesPerRequest)
        {
            _problems.Clear();
            _problems.Add($"Sending this would bring the conversation to {ByteSize.Format(ConversationBytes + PendingBytes)} of attachments; " +
                $"Beacon accepts {ByteSize.Format(Policy.MaxBytesPerRequest)}. Start a new chat to send it.");
            return;
        }

        List<AIContent> contents = [];
        if (!string.IsNullOrWhiteSpace(_draft))
        {
            contents.Add(new TextContent(_draft.Trim()));
        }

        contents.AddRange(_pending.Select(p => p.Content));
        _draft = string.Empty;
        _pending.Clear();
        _problems.Clear();

        await FocusAsync();
        await OnSend.InvokeAsync(new ChatMessage(ChatRole.User, contents));
    }
}
