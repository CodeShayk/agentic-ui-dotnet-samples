using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.AI;
using Ch17.Media;
using Microsoft.Extensions.AI;

namespace Ch17.Beacon.Agent;

/// <summary>
/// The scripted model's behaviors for this chapter, tried before <see cref="BeaconScripts.Default"/>.
/// The scripted model can't see or hear. It recognizes the three sample files in <c>samples/</c> by
/// their SHA-256 fingerprints and answers as a vision and audio model would; anything else gets an
/// honest "I can't see this" reply. A real multimodal model reads the pixels and the waveform instead.
/// </summary>
public static partial class MediaScripts
{
    /// <summary><c>samples/eq-601-nameplate.png</c>: EQ-601's nameplate.</summary>
    public const string NameplateSha256 = "2da355ad9154f78d0dd5ac28c46f4c8af45e44302ce064063b70626cc8a5a2ad";

    /// <summary><c>samples/eq-601-fan-grinding.wav</c>: five seconds of a grinding outdoor fan, synthesized.</summary>
    public const string FanRecordingSha256 = "c2fd150b3569eb68df7b733c1b8ed472c292657877f0b8af31b14b117bb13ccb";

    /// <summary><c>samples/hp-24s-service-bulletin.pdf</c>: an invented service bulletin for the HP-24S.</summary>
    public const string BulletinSha256 = "ad250ea199af020dda9f8cdbbbef7eb99080fcfc167a94f2e516ecca65a34289";

    /// <summary>The serial number printed on the sample nameplate.</summary>
    public const string NameplateSerial = "HP24S-2207-1184";

    public const string NameplateReading =
        "That's the nameplate of a Contoso Air HP-24S heat pump, the model of EQ-601 on WO-1003. I can read:\n\n" +
        "- Model: HP-24S\n" +
        "- Serial number: HP24S-2207-1184\n" +
        "- Manufactured: 2021-01\n\n" +
        "EQ-601 has no serial number on record. Shall I record HP24S-2207-1184 as EQ-601's serial number?";

    /// <summary>Everything attached was set aside by the media guard: say so, and say what Beacon takes.</summary>
    public static ScriptRule Refused { get; } = new("media-refused", Reply: ctx =>
    {
        if (Attachments(ctx).Count > 0 || RefusalNote().Matches(ctx.UserText) is not { Count: > 0 } notes)
        {
            return null;
        }

        var reasons = string.Join("; and ", notes.Select(n => $"attachment {n.Groups["part"].Value} because {n.Groups["reason"].Value}"));
        return ScriptReply.Say(
            $"I didn't receive what you attached: Beacon set aside {reasons}. " +
            $"I can take {MediaPolicy.Beacon.Describe()}.");
    });

    /// <summary>"Yes, record it" after Beacon offered to record a serial number it read.</summary>
    public static ScriptRule RecordSerial { get; } = new("record-serial",
        Reply: ctx =>
        {
            if (Attachments(ctx).Count > 0 || !ctx.Mentions("yes", "record", "go ahead", "confirm", "please do")
                || LastOffer(ctx) is not { } offer)
            {
                return null;
            }

            return ScriptReply.Call(EquipmentTools.RecordSerialNumberName,
                new Dictionary<string, object?> { ["equipmentId"] = offer.Groups["equipment"].Value, ["serialNumber"] = offer.Groups["serial"].Value });
        },
        Summarize: ctx =>
        {
            var result = ctx.ToolResults.FirstOrDefault(r => ctx.ToolNameFor(r) == EquipmentTools.RecordSerialNumberName);
            if (result is null)
            {
                return null;
            }

            using var json = JsonDocument.Parse(ScriptedChatClient.ResultJson(result));
            return json.RootElement.TryGetProperty("recorded", out _)
                ? ScriptReply.Say(
                    $"Done. {json.RootElement.GetProperty("equipmentId").GetString()} now has serial number " +
                    $"{json.RootElement.GetProperty("serialNumber").GetString()} on record, so Beacon can check its warranty with Contoso Air.")
                : ScriptReply.Say($"I couldn't record it: {json.RootElement.GetProperty("error").GetString()}");
        });

    /// <summary>A photo: read the sample nameplate, or admit the scripted model can't see.</summary>
    public static ScriptRule Nameplate { get; } = new("nameplate", Reply: ctx =>
        Attachments(ctx).FirstOrDefault(a => a.HasTopLevelMediaType("image")) is not { } image
            ? null
            : Fingerprint(image) == NameplateSha256
                ? ScriptReply.Say(NameplateReading)
                : ScriptReply.Say(CannotPerceive("photo", image)));

    /// <summary>A recording: diagnose the sample fan noise and check stock, or admit the scripted model can't hear.</summary>
    public static ScriptRule Recording { get; } = new("recording", Reply: ctx =>
    {
        if (Attachments(ctx).FirstOrDefault(a => a.HasTopLevelMediaType("audio")) is not { } audio)
        {
            return null;
        }

        if (Fingerprint(audio) != FanRecordingSha256)
        {
            return ScriptReply.Say(CannotPerceive("recording", audio));
        }

        return new ScriptReply
        {
            Reasoning =
                "The recording is five seconds of the outdoor unit. Under the steady motor hum there is a rasping burst " +
                "about fourteen times a second, which is the shaft speed of an 825 RPM fan. A noise that repeats once per " +
                "revolution comes from the rotating parts, so the fan motor's bearings come before the compressor.",
            Text =
                "I can hear a rasping grind once per fan revolution, on top of the normal motor hum. On an HP-24S that " +
                "points to worn outdoor fan motor bearings, not the compressor. Check the fan shaft for play: with little " +
                "play a bearing kit will do; with a lot, or a scored shaft, the motor needs replacing. Checking stock for both.",
            ToolCalls = [new ScriptToolCall(BeaconTools.SearchPartsName, new Dictionary<string, object?> { ["query"] = "fan", ["model"] = "HP-24S" })],
        };
    });

    /// <summary>A PDF: summarize the sample bulletin, or admit the scripted model can't read it.</summary>
    public static ScriptRule Bulletin { get; } = new("bulletin", Reply: ctx =>
        Attachments(ctx).FirstOrDefault(a => a.MediaType == MediaTypes.Pdf) is not { } pdf
            ? null
            : Fingerprint(pdf) == BulletinSha256
                ? ScriptReply.Say(
                    "That's Contoso Air service bulletin SB-HP24S-07, and it fits WO-1003. It covers a grinding noise from the " +
                    "outdoor fan of HP-24S units built 2021-01 through 2022-12, caused by early wear in the fan motor's bearings.\n\n" +
                    "1. Isolate power and check the fan shaft for radial play.\n" +
                    "2. Under 0.5 mm: replace the bearings with kit BRG-KIT-1.\n" +
                    "3. Over 0.5 mm, or a scored shaft: replace the motor (FAN-HP24).\n\n" +
                    "The repair is covered under the standard parts and labor warranty; quote the bulletin on the claim.")
                : ScriptReply.Say(CannotPerceive("document", pdf)));

    /// <summary>This chapter's rules, in the order they're tried. Declared after the rules: static initializers run top to bottom.</summary>
    public static IReadOnlyList<ScriptRule> Rules { get; } = [Refused, RecordSerial, Nameplate, Recording, Bulletin];

    private static string CannotPerceive(string what, DataContent content) =>
        $"Thanks, the {what} arrived ({ByteSize.Format(content.Data.Length)}). I'm Beacon's scripted stand-in, though, so I can't " +
        "actually see or hear attachments: I recognize only the sample files that come with Chapter 17. With a multimodal " +
        "model behind Beacon (Chapter 5 shows how to switch providers), I'd read it.";

    // The newest user message's attachments that passed the media guard.
    private static List<DataContent> Attachments(ScriptContext ctx) =>
        ctx.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Contents.OfType<DataContent>().ToList() ?? [];

    private static string Fingerprint(DataContent content) => Convert.ToHexStringLower(SHA256.HashData(content.Data.Span));

    private static Match? LastOffer(ScriptContext ctx) =>
        ctx.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant && !string.IsNullOrEmpty(m.Text)) is { } last
        && OfferPattern().Match(last.Text) is { Success: true } match
            ? match
            : null;

    [GeneratedRegex(@"Shall I record (?<serial>[A-Z0-9-]+) as (?<equipment>EQ-\d{3})'s serial number\?")]
    private static partial Regex OfferPattern();

    [GeneratedRegex(@"\[Beacon did not pass on attachment (?<part>\d+) of this message: (?<reason>.+?)\.\]")]
    private static partial Regex RefusalNote();
}
