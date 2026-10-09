using System.ComponentModel;
using Ch08.Field.Core.Presentation;

namespace Ch08.Beacon.Field;

public partial class ChatPage : ContentPage
{
    private readonly ChatViewModel _chat;

    public ChatPage(ChatViewModel chat)
    {
        InitializeComponent();
        BindingContext = _chat = chat;

        // MAUI's counterpart to an aria-live region: a finished answer, or advice after a broken one,
        // is spoken once by TalkBack, VoiceOver or Narrator.
        chat.Announced += (_, text) => SemanticScreenReader.Default.Announce(text);

        // When a turn ends, the next thing a technician does is type: put the cursor back.
        ((INotifyPropertyChanged)chat).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsBusy) && !_chat.IsBusy)
            {
                Composer.Focus();
            }
        };
    }
}
