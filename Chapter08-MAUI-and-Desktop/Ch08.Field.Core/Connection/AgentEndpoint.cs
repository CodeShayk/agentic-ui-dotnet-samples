namespace Ch08.Field.Core.Connection;

/// <summary>Where Beacon Field finds its agent, per platform, and when plain HTTP is allowed.</summary>
public static class AgentEndpoint
{
    /// <summary>The development agent's port (Chapter 8's agent listens on 5080).</summary>
    public const int DevelopmentPort = 5080;

    /// <summary>
    /// The agent's address: the configured one if there is one, otherwise the development agent as this
    /// platform reaches it. The Android emulator sees the host machine at 10.0.2.2; its own localhost is
    /// the emulator. Windows and Mac Catalyst share the host's localhost. The iOS simulator runs on a Mac and
    /// shares that Mac's localhost, which is the development machine only when the agent runs on the Mac.
    /// </summary>
    /// <param name="configured">An address from settings, or <see langword="null"/>.</param>
    /// <param name="platform">The platform the app runs on.</param>
    /// <param name="isEmulator">True on an emulator or simulator.</param>
    /// <param name="allowCleartext">True only in development builds.</param>
    /// <exception cref="InvalidOperationException">The address uses plain HTTP to a host other than a development host, or plain HTTP isn't allowed at all.</exception>
    public static Uri Resolve(string? configured, FieldPlatform platform, bool isEmulator, bool allowCleartext)
    {
        var endpoint = !string.IsNullOrWhiteSpace(configured)
            ? new Uri(configured, UriKind.Absolute)
            : new Uri($"http://{DevelopmentHost(platform, isEmulator)}:{DevelopmentPort}/agent");

        // Beacon Field uses SocketsHttpHandler, which talks to sockets directly. Android's cleartext policy
        // and iOS's App Transport Security are enforced by the platforms' own HTTP stacks, so they can't be
        // relied on here. The app polices itself: plain HTTP only to a development host, in a development build.
        if (endpoint.Scheme == Uri.UriSchemeHttp && !(allowCleartext && IsDevelopmentHost(endpoint)))
        {
            throw new InvalidOperationException(
                $"Beacon Field won't send work orders over plain HTTP to {endpoint.Host}. Use https:// (plain HTTP is for a development agent on this machine only).");
        }

        return endpoint;
    }

    private static string DevelopmentHost(FieldPlatform platform, bool isEmulator) =>
        platform == FieldPlatform.Android && isEmulator ? "10.0.2.2" : "localhost";

    private static bool IsDevelopmentHost(Uri endpoint) =>
        endpoint.IsLoopback || endpoint.Host == "10.0.2.2";
}

/// <summary>The platforms Beacon Field runs on, as far as endpoint resolution cares.</summary>
public enum FieldPlatform
{
    /// <summary>Windows (WinUI) and the WPF desktop client.</summary>
    Windows,

    /// <summary>Android, on a device or the emulator.</summary>
    Android,

    /// <summary>iOS, on a device or the simulator.</summary>
    iOS,

    /// <summary>Mac Catalyst.</summary>
    MacCatalyst,
}
