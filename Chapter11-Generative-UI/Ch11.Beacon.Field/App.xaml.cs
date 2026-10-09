namespace Ch11.Beacon.Field;

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
        var window = new Window(_services.GetRequiredService<MainPage>()) { Title = "Beacon Field" };
#if WINDOWS
        (window.Width, window.Height) = (480, 900); // a phone's proportions on a desktop
#endif
        return window;
    }
}
