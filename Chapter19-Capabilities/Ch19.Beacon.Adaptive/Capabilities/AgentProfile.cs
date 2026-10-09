using System.Text.Json;
using AGUI.Abstractions;

namespace Ch19.Beacon.Adaptive.Capabilities;

/// <summary>
/// What an agent said about one capability. Three values, not two: an omitted field means the agent
/// said nothing, and the specification forbids reading that as "can't".
/// </summary>
public enum Support
{
    /// <summary>Undeclared: the field was omitted, or there was no usable declaration at all.</summary>
    Unknown,

    /// <summary>Declared <c>true</c>.</summary>
    Supported,

    /// <summary>Declared <c>false</c>, explicitly.</summary>
    Unsupported,
}

public static class SupportExtensions
{
    /// <summary>The only place a nullable capability flag becomes a <see cref="Support"/>.</summary>
    public static Support ToSupport(this bool? declared) => declared switch
    {
        true => Support.Supported,
        false => Support.Unsupported,
        null => Support.Unknown,
    };
}

/// <summary>
/// The parts of an agent's declaration that Beacon's clients act on, already reduced to
/// <see cref="Support"/> values. Built from whatever discovery produced, including nothing.
/// </summary>
public sealed record AgentProfile
{
    /// <summary>The custom key under which a Beacon agent declares its attachment limits, in bytes per kind.</summary>
    public const string AttachmentLimitsKey = "contoso.beacon/attachmentLimits";

    private static readonly IReadOnlyDictionary<string, long> NoLimits = new Dictionary<string, long>();

    /// <summary>An agent that declared nothing: every capability unknown.</summary>
    public static AgentProfile Undeclared { get; } = new();

    public string? Name { get; init; }

    public string? Version { get; init; }

    public string? Provider { get; init; }

    /// <summary>Beacon's own deployment label from <c>identity.metadata.profile</c>, if present.</summary>
    public string? DeploymentProfile { get; init; }

    public Support ImageInput { get; init; }

    public Support AudioInput { get; init; }

    public Support PdfInput { get; init; }

    public Support Reasoning { get; init; }

    /// <summary>Reasoning happens but arrives only as encrypted values the user can't read.</summary>
    public bool ReasoningEncrypted { get; init; }

    /// <summary>Whether the agent accepts tools the client declares (frontend tools).</summary>
    public Support ClientTools { get; init; }

    /// <summary>The agent's attachment limits by kind (<c>image</c>, <c>audio</c>, <c>pdf</c>), from <c>custom</c>; empty when it didn't say.</summary>
    public IReadOnlyDictionary<string, long> AttachmentLimits { get; init; } = NoLimits;

    public static AgentProfile From(AgentCapabilities? capabilities)
    {
        if (capabilities is null)
        {
            return Undeclared;
        }

        var input = capabilities.Multimodal?.Input;
        var tools = capabilities.Tools;
        return new AgentProfile
        {
            Name = capabilities.Identity?.Name,
            Version = capabilities.Identity?.Version,
            Provider = capabilities.Identity?.Provider,
            DeploymentProfile = ReadString(capabilities.Identity?.Metadata, "profile"),
            ImageInput = input?.Image.ToSupport() ?? Support.Unknown,
            AudioInput = input?.Audio.ToSupport() ?? Support.Unknown,
            PdfInput = input?.Pdf.ToSupport() ?? Support.Unknown,
            Reasoning = capabilities.Reasoning?.Supported.ToSupport() ?? Support.Unknown,
            ReasoningEncrypted = capabilities.Reasoning?.Encrypted == true,

            // tools.supported: false switches tool calling off altogether, whatever clientProvided says.
            ClientTools = tools?.Supported == false ? Support.Unsupported : tools?.ClientProvided.ToSupport() ?? Support.Unknown,
            AttachmentLimits = ReadLimits(capabilities.Custom),
        };
    }

    private static string? ReadString(JsonElement? obj, string name) =>
        obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // custom is open by key and the protocol gives it no meaning, so read it defensively: a value of
    // the wrong shape is simply "not said".
    private static IReadOnlyDictionary<string, long> ReadLimits(JsonElement? custom)
    {
        if (custom is not { ValueKind: JsonValueKind.Object } c
            || !c.TryGetProperty(AttachmentLimitsKey, out var limits) || limits.ValueKind != JsonValueKind.Object)
        {
            return NoLimits;
        }

        return limits.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var n) && n > 0)
            .ToDictionary(p => p.Name, p => p.Value.GetInt64());
    }
}
