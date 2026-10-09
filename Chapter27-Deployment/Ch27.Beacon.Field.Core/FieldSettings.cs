using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ch27.Beacon.Field.Core;

/// <summary>Where Beacon Field's agent is and what it asks Microsoft Entra ID for to call it.</summary>
public sealed record FieldAgentSettings(Uri Endpoint, IReadOnlyList<string> Scopes);

/// <summary>The settings this build of Beacon Field was made for.</summary>
public static class FieldSettings
{
    /// <summary>
    /// Reads the one settings file the build embedded (<c>-p:BeaconEnvironment=...</c>). There is no fallback:
    /// an app that can't find its agent fails here, at startup, rather than calling somewhere else.
    /// </summary>
    public static FieldAgentSettings Load()
    {
        using var stream = typeof(FieldSettings).Assembly.GetManifestResourceStream("appsettings.json")
            ?? throw new InvalidOperationException("This build of Beacon Field has no embedded appsettings.json.");
        var file = JsonSerializer.Deserialize(stream, FieldSettingsJsonContext.Default.SettingsFile)
            ?? throw new InvalidOperationException("Beacon Field's appsettings.json is empty.");

        return new FieldAgentSettings(new Uri(file.BeaconAgent.Endpoint, UriKind.Absolute), file.BeaconAgent.Scopes);
    }

    internal sealed record SettingsFile(AgentSection BeaconAgent);

    internal sealed record AgentSection(string Endpoint, string[] Scopes);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FieldSettings.SettingsFile))]
internal sealed partial class FieldSettingsJsonContext : JsonSerializerContext;
