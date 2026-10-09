using AGUI.Client;
using Beacon.Client.Core;
using Ch17.Beacon.Field.Devices;
using Ch17.Field.Capture;
using Ch17.Media;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch17.Beacon.Field;

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

        // The device, behind Ch17.Field.Capture's interfaces: MAUI Essentials and Windows APIs here, fakes in the tests.
        builder.Services.AddSingleton(MediaPolicy.Beacon);
        builder.Services.AddSingleton<IPhotoSource, EssentialsPhotoSource>();
        builder.Services.AddSingleton<IFileSource, EssentialsFileSource>();
#if WINDOWS
        builder.Services.AddSingleton<IAudioRecorder, WindowsAudioRecorder>();
#else
        builder.Services.AddSingleton<IAudioRecorder, NoAudioRecorder>();
#endif
        builder.Services.AddSingleton<AttachmentTray>();
        builder.Services.AddSingleton<FieldCapture>();

        // One technician, one conversation, as in Chapter 8. No header timeout (Chapter 6): the conversation's turn
        // timeout bounds the upload of up to 20 MB of attachments and the run alike.
        builder.Services.AddSingleton(_ => AgentHttp.Create());
        builder.Services.AddSingleton(services => WireEventTap.ForHttp(services.GetRequiredService<HttpClient>(), AgentEndpoint()));
        builder.Services.AddSingleton<IChatClient>(services =>
            new AGUIChatClient(new AGUIChatClientOptions { Transport = services.GetRequiredService<WireEventTap>() }));
        builder.Services.AddSingleton(services => new AgentConversation(
            services.GetRequiredService<IChatClient>(),
            new ConversationOptions
            {
                Tap = services.GetRequiredService<WireEventTap>(),
                Logger = services.GetRequiredService<ILogger<AgentConversation>>(),
            }));

        builder.Services.AddSingleton<ChatPage>();

        return builder.Build();
    }

    // Chapter 17's agent on this machine. The Android emulator reaches the host as 10.0.2.2.
    private static string AgentEndpoint() => Preferences.Default.Get(
        "BeaconAgentEndpoint",
        DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:5170/agent" : "http://localhost:5170/agent");
}
