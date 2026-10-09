using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using Ch17.Media;
using Microsoft.Extensions.AI;

namespace Ch17.Beacon.Agent.Media;

/// <summary>One media part Beacon didn't pass to the model, as the client hears about it.</summary>
/// <param name="MessageId">The user message the part belongs to.</param>
/// <param name="Part">The part's index in that message's <c>content</c> array.</param>
/// <param name="Code">Why, as a stable code: not-accepted, type-mismatch, too-large, url-refused, provider-file.</param>
/// <param name="Reason">Why, in Beacon's words.</param>
public sealed record MediaRefusal(string MessageId, int Part, string Code, string Reason);

/// <summary>The value of the <c>beacon.media_refused</c> <c>CUSTOM</c> event.</summary>
public sealed record MediaRefusalReport(IReadOnlyList<MediaRefusal> Refused);

/// <summary>
/// The last check before the model. Every media part in every user message is inspected: an accepted
/// type, within its limit, with bytes that match. URL sources are fetched through
/// <see cref="MediaUrlFetcher"/> and become inline data. A part that fails is not passed on: the model
/// gets a note written by Beacon in its place, and the client gets a <c>CUSTOM</c> event naming the part.
/// The run goes on, as the protocol requires of a producer handed content it can't use.
/// </summary>
/// <remarks>Place it outermost, so the event is the first thing the run streams and every layer below sees only screened content.</remarks>
public sealed partial class MediaGuardChatClient(
    IChatClient innerClient, MediaPolicy policy, MediaUrlFetcher fetcher, ILogger<MediaGuardChatClient> logger)
    : DelegatingChatClient(innerClient)
{
    public const string RefusedEventName = "beacon.media_refused";

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (screened, _) = await ScreenAsync(messages, cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(screened, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (screened, refusals) = await ScreenAsync(messages, cancellationToken).ConfigureAwait(false);

        if (refusals.Count > 0)
        {
            // A BaseEvent in RawRepresentation is written to the stream as it is, right after RUN_STARTED.
            yield return new ChatResponseUpdate
            {
                RawRepresentation = new CustomEvent
                {
                    Name = RefusedEventName,
                    Value = JsonSerializer.SerializeToElement(new MediaRefusalReport(refusals), Ch17JsonContext.Default.MediaRefusalReport),
                },
            };
        }

        await foreach (var update in base.GetStreamingResponseAsync(screened, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private async Task<(List<ChatMessage> Messages, List<MediaRefusal> Refusals)> ScreenAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        List<ChatMessage> screened = [];
        List<MediaRefusal> refusals = [];
        long fetchedBytes = 0;

        foreach (var message in messages)
        {
            if (message.Role != ChatRole.User || !message.Contents.Any(IsMedia))
            {
                screened.Add(message);
                continue;
            }

            List<AIContent> contents = [];
            var number = 0; // attachments are numbered from 1 for people; the event keeps the content index
            for (var part = 0; part < message.Contents.Count; part++)
            {
                var content = message.Contents[part];
                if (!IsMedia(content))
                {
                    contents.Add(content);
                    continue;
                }

                number++;
                var (accepted, code, reason) = content switch
                {
                    DataContent data => Check(data.MediaType, data.Data),
                    UriContent uri => await FetchAsync(uri, cancellationToken).ConfigureAwait(false),
                    _ => (null, MediaRefusalCode.ProviderFile, "it is a file handle from a model provider, and Beacon can't use those"),
                };

                if (accepted is not null && content is UriContent && (fetchedBytes += accepted.Data.Length) > policy.MaxBytesPerRequest)
                {
                    (accepted, code, reason) = (null, MediaRefusalCode.TooLarge, "the conversation's attachments would exceed Beacon's per-request limit");
                }

                if (accepted is not null)
                {
                    // A fresh DataContent with Beacon's own name: the client's file name and part metadata stay
                    // behind, and so does a photo's Exif, whatever a client did or didn't strip.
                    var data = MediaMetadata.Strip(accepted.Data, accepted.MediaType);
                    contents.Add(new DataContent(data, accepted.MediaType) { Name = $"attachment-{number}" });
                    continue;
                }

                var refusal = new MediaRefusal(message.MessageId ?? string.Empty, part, CodeName(code!.Value), reason!);
                refusals.Add(refusal);
                LogRefused(logger, refusal.MessageId, part, refusal.Code);

                // The model learns that something was attached and why it isn't here, in words Beacon wrote.
                contents.Add(new TextContent($"\n[Beacon did not pass on attachment {number} of this message: {reason}.]"));
            }

            screened.Add(new ChatMessage(message.Role, contents)
            {
                MessageId = message.MessageId,
                AuthorName = message.AuthorName,
                AdditionalProperties = message.AdditionalProperties,
            });
        }

        return (screened, refusals);
    }

    private (DataContent? Accepted, MediaRefusalCode? Code, string? Reason) Check(string mediaType, ReadOnlyMemory<byte> data)
    {
        var verdict = MediaInspection.Inspect(mediaType, data.Span, policy);
        return verdict.Accepted ? (new DataContent(data, verdict.Rule!.MediaType), null, null) : (null, verdict.Code, verdict.Reason);
    }

    private async Task<(DataContent? Accepted, MediaRefusalCode? Code, string? Reason)> FetchAsync(UriContent uri, CancellationToken cancellationToken)
    {
        var result = await fetcher.FetchAsync(uri.Uri, uri.MediaType, cancellationToken).ConfigureAwait(false);
        return (result.Content, result.Code, result.Reason);
    }

    private static bool IsMedia(AIContent content) => content is DataContent or UriContent or HostedFileContent;

    private static string CodeName(MediaRefusalCode code) => code switch
    {
        MediaRefusalCode.NotAccepted => "not-accepted",
        MediaRefusalCode.TypeMismatch => "type-mismatch",
        MediaRefusalCode.TooLarge => "too-large",
        MediaRefusalCode.UrlRefused => "url-refused",
        _ => "provider-file",
    };

    [LoggerMessage(EventId = 1701, Level = LogLevel.Warning,
        Message = "Refused media part {Part} of message {MessageId}: {Code}.")]
    private static partial void LogRefused(ILogger logger, string messageId, int part, string code);
}
