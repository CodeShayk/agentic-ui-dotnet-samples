using AGUI.Client;
using Beacon.Client.Core;
using Ch11.Beacon.ComponentViews;
using Microsoft.Extensions.AI;

namespace Ch11.Beacon.Field;

public static class MauiProgram
{
    /// <summary>The agent on the development machine. Chapter 8 resolves this per platform (emulators, phones).</summary>
    private const string AgentEndpoint = "http://localhost:5110/agent";

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

        // Chapter 7's client stack, the same as the Desk's: transport, tap, AGUIChatClient, conversation.
        builder.Services.AddSingleton(_ => AgentHttp.Create());
        builder.Services.AddSingleton(services => WireEventTap.ForHttp(services.GetRequiredService<HttpClient>(), AgentEndpoint));
        builder.Services.AddSingleton<IChatClient>(services =>
            new AGUIChatClient(new AGUIChatClientOptions { Transport = services.GetRequiredService<WireEventTap>() }));
        builder.Services.AddSingleton(services => new AgentConversation(
            services.GetRequiredService<IChatClient>(),
            new ConversationOptions { Tap = services.GetRequiredService<WireEventTap>() }));

        // The same projector and component kinds as the Desk. Only the templates are the Field's own.
        builder.Services.AddSingleton(_ => new ComponentProjector(ComponentKinds.All));
        builder.Services.AddSingleton<FieldChatViewModel>();
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
