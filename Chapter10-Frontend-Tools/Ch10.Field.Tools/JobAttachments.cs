using System.Collections.Concurrent;

namespace Ch10.Field.Tools;

/// <summary>A photo kept on the device for a job.</summary>
public sealed record Attachment(string PhotoId, string? WorkOrderId, string Subject, CapturedPhoto Photo, DateTimeOffset CapturedAt);

/// <summary>
/// Photos the technician took for Beacon. They stay on the device: <c>take_photo</c> tells the agent a
/// photo exists and what it's of, not what's in it (Chapter 17 sends images to the model).
/// </summary>
public sealed class JobAttachments(TimeProvider? timeProvider = null)
{
    private readonly ConcurrentDictionary<string, Attachment> _attachments = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Raised after an attachment is added, on the thread that added it.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<Attachment> All => [.. _attachments.Values.OrderBy(a => a.CapturedAt)];

    public Attachment Add(string? workOrderId, string subject, CapturedPhoto photo)
    {
        var attachment = new Attachment($"photo-{Guid.NewGuid():N}"[..18], workOrderId, subject, photo, _time.GetUtcNow());
        _attachments[attachment.PhotoId] = attachment;
        Changed?.Invoke(this, EventArgs.Empty);
        return attachment;
    }
}
