using Ch08.Field.Core;
using Ch08.Field.Core.Presentation;

namespace Ch08.Beacon.Field;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Resolved here, not in the constructor: the page's XAML uses the resources InitializeComponent loaded.
        var chat = _services.GetRequiredService<ChatViewModel>();
        var field = _services.GetRequiredService<FieldClient>();
        var window = new Window(_services.GetRequiredService<ChatPage>()) { Title = "Beacon Field" };
#if WINDOWS
        // On a desktop, open at a phone's proportions: the layout is designed for one hand.
        (window.Width, window.Height) = (440, 860);
#endif

        // The app's lifecycle, as the view model needs it. Stopped is the app leaving the screen (the
        // phone's home button, another app, a minimized window); Resumed is its return.
        window.Stopped += (_, _) => chat.OnBackgrounded();
        window.Resumed += (_, _) => chat.OnResumed();

        // Closing the app stops any run, so the agent stops its model instead of talking to nobody.
        window.Destroying += (_, _) => field.Dispose();

        // The device's own view of the network: Send waits for a connection rather than failing three times.
        // The event may arrive off the UI thread; SetOnline posts its change through the dispatcher.
        chat.SetOnline(Connectivity.Current.NetworkAccess != NetworkAccess.None);
        Connectivity.Current.ConnectivityChanged += (_, e) => chat.SetOnline(e.NetworkAccess != NetworkAccess.None);

        return window;
    }
}
