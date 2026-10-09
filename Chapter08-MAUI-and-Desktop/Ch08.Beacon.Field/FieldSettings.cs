using Ch08.Field.Core.Connection;

namespace Ch08.Beacon.Field;

/// <summary>Beacon Field's settings and what it knows about the device it runs on.</summary>
public static class FieldSettings
{
    /// <summary>The preference key for the agent's address, set by an administrator or a deployment profile.</summary>
    public const string EndpointKey = "BeaconAgentEndpoint";

    /// <summary>
    /// The agent's address: the <c>BEACON_AGENT_ENDPOINT</c> environment variable (handy on a desktop),
    /// then the app's preferences, then <see langword="null"/> for the development default.
    /// </summary>
    public static string? ConfiguredEndpoint =>
        Environment.GetEnvironmentVariable("BEACON_AGENT_ENDPOINT") is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : Preferences.Default.Get<string?>(EndpointKey, null);

    /// <summary>The platform, as endpoint resolution sees it.</summary>
    public static FieldPlatform Platform =>
        DeviceInfo.Current.Platform == DevicePlatform.Android ? FieldPlatform.Android
        : DeviceInfo.Current.Platform == DevicePlatform.iOS ? FieldPlatform.iOS
        : DeviceInfo.Current.Platform == DevicePlatform.MacCatalyst ? FieldPlatform.MacCatalyst
        : FieldPlatform.Windows;

    /// <summary>True on Android and iOS, whose apps are suspended in the background.</summary>
    public static bool IsPhone => Platform is FieldPlatform.Android or FieldPlatform.iOS;

    /// <summary>True in a debug build: the only builds allowed to use plain HTTP to a development agent.</summary>
    public static bool IsDevelopmentBuild =>
#if DEBUG
        true;
#else
        false;
#endif
}
