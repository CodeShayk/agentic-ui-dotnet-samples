using System.Globalization;
using Microsoft.Extensions.AI;

namespace Ch19.Beacon.Adaptive.Planning;

/// <summary>A captured or uploaded file waiting to be sent.</summary>
public sealed record MediaAttachment(string FileName, string MediaType, ReadOnlyMemory<byte> Data);

/// <summary>
/// Where a client gets media from: the camera, the microphone, a file picker. Beacon Field implements
/// it with MAUI Essentials; tests use fakes that return media generated in code.
/// </summary>
public interface IMediaCapture
{
    /// <summary>Captures media for an input feature; <see langword="null"/> when the user cancelled.</summary>
    Task<MediaAttachment?> CaptureAsync(BeaconFeature feature, CancellationToken cancellationToken = default);
}

/// <summary>
/// The attachments for the next message. It accepts only what the plan offers (declared or unknown),
/// within the agent's size limit, and turns text plus attachments into one user <see cref="ChatMessage"/>.
/// </summary>
public sealed class AttachmentTray(FeaturePlan plan)
{
    private readonly List<MediaAttachment> _items = [];

    public IReadOnlyList<MediaAttachment> Items => _items;

    public FeaturePlan Plan { get; } = plan;

    /// <summary>The input feature a media type belongs to, or <see langword="null"/> for types Beacon never sends.</summary>
    public static BeaconFeature? FeatureFor(string mediaType) => mediaType switch
    {
        _ when mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => BeaconFeature.PhotoInput,
        _ when mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => BeaconFeature.VoiceNote,
        _ when mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) => BeaconFeature.DocumentInput,
        _ => null,
    };

    /// <summary>Adds an attachment, or returns the reason it can't be sent to this agent.</summary>
    public string? TryAdd(MediaAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (FeatureFor(attachment.MediaType) is not { } feature || !Plan.Has(feature))
        {
            return $"Beacon doesn't send {attachment.MediaType} files.";
        }

        var decision = Plan[feature];
        if (!decision.IsOffered)
        {
            return decision.Note ?? "This agent doesn't accept that kind of attachment.";
        }

        var limit = Plan.MaxAttachmentBytes(feature);
        if (attachment.Data.Length > limit)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{attachment.FileName} is {Megabytes(attachment.Data.Length)}; this agent accepts up to {Megabytes(limit)} for this kind of attachment.");
        }

        _items.Add(attachment);
        return null;
    }

    public void Remove(MediaAttachment attachment) => _items.Remove(attachment);

    public void Clear() => _items.Clear();

    /// <summary>The user message for the next turn: the text first, then each attachment as <see cref="DataContent"/>.</summary>
    public ChatMessage ToMessage(string text)
    {
        List<AIContent> contents = string.IsNullOrWhiteSpace(text) ? [] : [new TextContent(text)];
        contents.AddRange(_items.Select(a => new DataContent(a.Data, a.MediaType) { Name = a.FileName }));
        return new ChatMessage(ChatRole.User, contents);
    }

    private static string Megabytes(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d / 1024d:0.#} MB");
}
