using System.Net.Http;
using System.Windows;
using Ch08.Field.Core;
using Ch08.Field.Core.Connection;
using Ch08.Field.Core.Presentation;

namespace Ch08.Beacon.Wpf;

public partial class App : Application
{
    private HttpClient? _http;
    private FieldClient? _field;
    private ChatViewModel? _chat;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The agent's address: the first argument, BEACON_AGENT_ENDPOINT or the development agent.
        var configured = e.Args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("BEACON_AGENT_ENDPOINT");
        var endpoint = AgentEndpoint.Resolve(configured, FieldPlatform.Windows, isEmulator: false, allowCleartext: true);

        _http = FieldHttp.CreateClient(connectTimeout: TimeSpan.FromSeconds(10));
        _field = FieldClient.Create(_http, endpoint);

        // The same view model as Beacon Field. Only the dispatcher and the window are WPF's.
        _chat = new ChatViewModel(_field.Conversation, new WpfDispatcher(Dispatcher), new ChatViewModelOptions { Transport = _field.Transport });

        MainWindow = new MainWindow(_chat) { Title = $"Beacon — {endpoint}" };
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Closing the window stops any run, so the agent stops its model.
        _chat?.Dispose();
        _field?.Dispose();
        _http?.Dispose();
        base.OnExit(e);
    }
}
