using Ch19.Beacon.Adaptive.Connection;
using Ch19.Beacon.Adaptive.Planning;
using Ch19.Beacon.Field.Devices;
using Microsoft.Extensions.Logging;

namespace Ch19.Beacon.Field;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        // The device, behind IMediaCapture: MAUI Essentials here, fakes in the tests.
        builder.Services.AddSingleton<IMediaCapture, EssentialsMediaCapture>();
        // No header timeout and a bounded connect (Chapter 6); the conversation bounds the turn.
        builder.Services.AddSingleton(_ => AgentHttp.CreateClient());
        builder.Services.AddSingleton(services => new FieldSession(
            services.GetRequiredService<HttpClient>(), AgentEndpoint(), services.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton<ChatPage>();

        return builder.Build();
    }

    // Chapter 19's agent on this machine. The Android emulator reaches the host as 10.0.2.2.
    private static Uri AgentEndpoint() => new(Preferences.Default.Get(
        "BeaconAgentEndpoint",
        DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:5190/agent" : "http://localhost:5190/agent"));
}
