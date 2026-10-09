using AGUI.Client;
using Beacon.Client.Core;
using Ch10.Beacon.Field.Devices;
using Ch10.Field.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch10.Beacon.Field;

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

        // The device, behind Ch10.Field.Tools' interfaces: MAUI Essentials here, fakes in the tests.
        builder.Services.AddSingleton<ILocationSource, EssentialsLocationSource>();
        builder.Services.AddSingleton<ICameraCapture, EssentialsCameraCapture>();
        builder.Services.AddSingleton<IDevicePermissions, EssentialsPermissions>();
        builder.Services.AddSingleton<IConsentPrompt, DialogConsentPrompt>();
        builder.Services.AddSingleton<IPartLabelReader, ManualLabelReader>();
        builder.Services.AddSingleton<SessionConsent>();
        builder.Services.AddSingleton<JobAttachments>();
        builder.Services.AddSingleton<FieldTools>();

        // One technician, one conversation. AGUIChatClient runs the Field tools with its own function
        // invocation; nothing here adds another.
        builder.Services.AddSingleton(_ => AgentHttp.Create());
        builder.Services.AddSingleton(services => WireEventTap.ForHttp(services.GetRequiredService<HttpClient>(), AgentEndpoint()));
        builder.Services.AddSingleton<IChatClient>(services =>
            new AGUIChatClient(new AGUIChatClientOptions { Transport = services.GetRequiredService<WireEventTap>() }));
        builder.Services.AddSingleton(services => new AgentConversation(
            services.GetRequiredService<IChatClient>(),
            new ConversationOptions
            {
                Tools = services.GetRequiredService<FieldTools>().Create(),
                Tap = services.GetRequiredService<WireEventTap>(),
                Logger = services.GetRequiredService<ILogger<AgentConversation>>(),
            }));

        builder.Services.AddSingleton<ChatPage>();

        return builder.Build();
    }

    // Chapter 10's agent on this machine. The Android emulator reaches the host as 10.0.2.2.
    private static string AgentEndpoint() => Preferences.Default.Get(
        "BeaconAgentEndpoint",
        DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:5100/agent" : "http://localhost:5100/agent");
}
