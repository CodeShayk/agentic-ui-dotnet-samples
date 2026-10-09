using AGUI.Abstractions;
using Ch19.Beacon.Agent.Capabilities;

namespace Ch19.Beacon.Agent.Enforcement;

/// <summary>
/// Holds every run's input to the deployment's declaration. A client that adapted to the capabilities
/// document never trips it; a client that didn't (or a request typed by hand) gets a clear refusal
/// instead of a model that quietly can't see the photo.
/// </summary>
public sealed partial class DeclaredInputGuard(BeaconDeployment deployment, ILogger<DeclaredInputGuard> logger)
{
    public IResult? Admit(RunAgentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // clientProvided: false. The tools are dropped, not refused: the run can still be answered
        // with the agent's own tools, and the document already told the client not to send them.
        if (!deployment.AcceptsClientTools && input.Tools is { Count: > 0 } tools)
        {
            LogClientToolsIgnored(logger, tools.Count, input.RunId);
            input.Tools = [];
        }

        var newest = input.Messages.OfType<AGUIUserMessage>().LastOrDefault();
        foreach (var message in input.Messages.OfType<AGUIUserMessage>().Where(m => !m.Content.IsText))
        {
            List<AGUIInputContent> parts = [.. message.Content];
            var refused = parts.OfType<AGUIMediaInputContent>().Select(Refusal).FirstOrDefault(r => r is not null);
            if (refused is null)
            {
                continue;
            }

            // The new message: refuse the run before it starts, saying exactly what isn't accepted.
            if (ReferenceEquals(message, newest))
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Attachment not accepted", detail: refused);
            }

            // An older message (sent before a redeployment, say): drop the part, so one old photo
            // doesn't fail every later turn of the thread.
            message.Content = new AGUIContent([.. parts.Where(p => p is not AGUIMediaInputContent media || Refusal(media) is null)]);
            LogDroppedFromHistory(logger, message.Id, input.RunId);
        }

        return null;
    }

    private string? Refusal(AGUIMediaInputContent part)
    {
        var mimeType = part.Source switch
        {
            AGUIInputContentDataSource data => data.MimeType,
            AGUIInputContentUrlSource url => url.MimeType,
            _ => null,
        };

        var (name, accepted) = part switch
        {
            AGUIImageInputContent => ("image", deployment.AcceptsImages),
            AGUIAudioInputContent => ("audio", deployment.AcceptsAudio),
            AGUIVideoInputContent => ("video", false),
            AGUIDocumentInputContent when string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase) => ("pdf", deployment.AcceptsPdf),
            _ => ("file", false),
        };

        if (!accepted)
        {
            return $"This Beacon deployment doesn't accept {name} input (multimodal.input.{name} is false in /agent/capabilities).";
        }

        // Inline data only; a URL source is a request the server would make on the client's behalf
        // (Chapter 17 covers what that takes).
        if (part.Source is not AGUIInputContentDataSource source)
        {
            return "Beacon accepts attachments as inline data only.";
        }

        var decodedBytes = source.Value.Length / 4L * 3;
        var limit = deployment.AttachmentLimits[name];
        return decodedBytes > limit
            ? $"An {name} attachment of about {decodedBytes / 1024} KB is larger than this deployment's {limit / 1024} KB limit for {name}."
            : null;
    }

    [LoggerMessage(EventId = 1911, Level = LogLevel.Information,
        Message = "Run {RunId}: ignored {Count} client-declared tools; this deployment declares clientProvided: false.")]
    private static partial void LogClientToolsIgnored(ILogger logger, int count, string runId);

    [LoggerMessage(EventId = 1912, Level = LogLevel.Information,
        Message = "Run {RunId}: dropped attachments this deployment doesn't accept from earlier message {MessageId}.")]
    private static partial void LogDroppedFromHistory(ILogger logger, string messageId, string runId);
}
