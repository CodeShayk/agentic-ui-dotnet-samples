using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook;

/// <summary>The server's record of a confirmation it asked for: what it asked and until when the answer counts.</summary>
public sealed record PendingConfirmation(string Id, string Question, DateTimeOffset ExpiresAt);

/// <summary>
/// A pipeline layer's request for a dispatcher's confirmation. Only the ID is serialized; the record stays
/// on the server, because whatever this content serializes can reach the client as <c>rawEvent</c>.
/// </summary>
public sealed class ConfirmationRequestContent : AIContent
{
    public required string ConfirmationId { get; init; }

    [JsonIgnore]
    public PendingConfirmation? Request { get; init; }
}

/// <summary>Recipe: <see cref="AGUIStreamOptions.MapInterrupt"/> turns the pipeline's own request into an interrupt.</summary>
public static class MapInterruptRecipe
{
    /// <summary>The <c>$type</c> discriminator <see cref="ConfirmationRequestContent"/> serializes with.</summary>
    public const string ConfirmationDiscriminator = "beaconConfirmation";

    private static readonly JsonElement ConfirmSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"confirmed":{"type":"boolean"}},"required":["confirmed"],"additionalProperties":false}""")
        .RootElement.Clone();

    public static AGUIStreamOptions Apply(AGUIStreamOptions options) => options
        .MapInterrupt(content => content is ConfirmationRequestContent { Request: { } request }
            ? new AGUIInterrupt
            {
                Id = request.Id,
                Reason = InterruptReasons.Confirmation,
                Message = request.Question,
                ResponseSchema = ConfirmSchema,
                ExpiresAt = request.ExpiresAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            }
            : null);
}
