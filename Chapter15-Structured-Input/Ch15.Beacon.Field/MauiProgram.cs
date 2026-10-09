using AGUI.Client;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch15.Beacon.Field;

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

        // One clock for the conversation's expiry checks and the forms' expiry display.
        builder.Services.AddSingleton(TimeProvider.System);

        // One technician, one conversation: Chapter 7's stack, as in Chapter 10.
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
                TimeProvider = services.GetRequiredService<TimeProvider>(),
            }));

        builder.Services.AddSingleton<ChatPage>();

        return builder.Build();
    }

    // Chapter 15's agent on this machine. The Android emulator reaches the host as 10.0.2.2.
    private static string AgentEndpoint() => Preferences.Default.Get(
        "BeaconAgentEndpoint",
        DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:5150/agent" : "http://localhost:5150/agent");
}
