using Ch08.Field.Core;
using Ch08.Field.Core.Connection;

namespace Ch08.Beacon.Field.Hybrid;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Blazor components in a native window: the BlazorWebView.
        builder.Services.AddMauiBlazorWebView();

        // The same client stack as the XAML app, registered for injection into components.
        var endpoint = AgentEndpoint.Resolve(
            Environment.GetEnvironmentVariable("BEACON_AGENT_ENDPOINT"), FieldPlatform.Windows, isEmulator: false, allowCleartext: true);
        builder.Services.AddSingleton(_ => FieldHttp.CreateClient(connectTimeout: TimeSpan.FromSeconds(10)));
        builder.Services.AddSingleton(services => FieldClient.Create(services.GetRequiredService<HttpClient>(), endpoint));

        return builder.Build();
    }
}
