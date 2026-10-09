using Ch08.Field.Core;
using Ch08.Field.Core.Connection;
using Ch08.Field.Core.Presentation;
using Microsoft.Extensions.Logging;

namespace Ch08.Beacon.Field;

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

        // Where the agent is, as this platform reaches it. Plain HTTP only in a debug build.
        var endpoint = AgentEndpoint.Resolve(
            FieldSettings.ConfiguredEndpoint,
            FieldSettings.Platform,
            isEmulator: DeviceInfo.Current.DeviceType == DeviceType.Virtual,
            allowCleartext: FieldSettings.IsDevelopmentBuild);

        // One HttpClient and one conversation for the app's lifetime.
        builder.Services.AddSingleton(_ => FieldHttp.CreateClient(connectTimeout: TimeSpan.FromSeconds(10)));
        builder.Services.AddSingleton(services => FieldClient.Create(
            services.GetRequiredService<HttpClient>(),
            endpoint,
            new FieldClientOptions { Logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Beacon.Field") }));

        builder.Services.AddSingleton<IUiDispatcher, MauiDispatcher>();
        builder.Services.AddSingleton(services =>
        {
            var field = services.GetRequiredService<FieldClient>();
            return new ChatViewModel(field.Conversation, services.GetRequiredService<IUiDispatcher>(), new ChatViewModelOptions
            {
                Transport = field.Transport,
                // Phones suspend background apps; a desktop window keeps running when minimized.
                BackgroundGracePeriod = FieldSettings.IsPhone ? TimeSpan.FromSeconds(15) : null,
            });
        });
        builder.Services.AddSingleton<ChatPage>();

        return builder.Build();
    }
}
