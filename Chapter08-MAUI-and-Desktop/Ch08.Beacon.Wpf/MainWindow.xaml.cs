using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Ch08.Field.Core.Presentation;

namespace Ch08.Beacon.Wpf;

public partial class MainWindow : Window
{
    private readonly ChatViewModel _chat;
    private bool _following = true;

    public MainWindow(ChatViewModel chat)
    {
        InitializeComponent();
        DataContext = _chat = chat;

        // Speak a finished answer, or the advice after a broken one, once, through a polite live region.
        chat.Announced += (_, text) =>
        {
            Announcer.Text = text;
            (UIElementAutomationPeer.FromElement(Announcer) ?? UIElementAutomationPeer.CreatePeerForElement(Announcer))
                .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        };

        // Keep the newest text in view while the reader is at the bottom; leave them alone if they scrolled up.
        Scroller.ScrollChanged += (_, e) =>
        {
            if (e.ExtentHeightChange == 0)
            {
                _following = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 4;
            }
            else if (_following)
            {
                Scroller.ScrollToEnd();
            }
        };

        // When a turn ends, put the cursor back in the message box.
        ((INotifyPropertyChanged)chat).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsBusy) && !_chat.IsBusy)
            {
                Composer.Focus();
            }
        };

        // A minimized window keeps running (the desktop has no grace period), but announcements wait for it.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                _chat.OnBackgrounded();
            }
            else
            {
                _chat.OnResumed();
            }
        };

        Loaded += (_, _) => Composer.Focus();
    }
}
