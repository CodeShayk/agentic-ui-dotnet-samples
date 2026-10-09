namespace Ch08.Beacon.Field.Hybrid;

public partial class App : Application
{
    public App() => InitializeComponent();

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = "Beacon Field (Blazor Hybrid)", Width = 440, Height = 860 };
}
