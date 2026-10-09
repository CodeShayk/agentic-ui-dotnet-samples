namespace Ch11.Beacon.Field;

public partial class MainPage : ContentPage
{
    public MainPage(FieldChatViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
