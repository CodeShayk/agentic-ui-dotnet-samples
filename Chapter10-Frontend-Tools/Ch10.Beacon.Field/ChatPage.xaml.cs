using Beacon.Client.Core;

namespace Ch10.Beacon.Field;

/// <summary>
/// Beacon Field's chat. It renders the conversation's view state and sends through it; the frontend
/// tools run inside the turn, on a background thread, and reach the technician through the app's dialogs.
/// </summary>
public partial class ChatPage : ContentPage
{
    private static readonly string[] Prompts =
    [
        "Am I at the right site for WO-1001?",
        "Scan the part label and check stock",
        "Take a photo of the nameplate for WO-1001",
    ];

    private readonly AgentConversation _conversation;
    private int _renderQueued;
    private ConversationStatus _lastStatus;

    public ChatPage(AgentConversation conversation)
    {
        InitializeComponent();
        _conversation = conversation;

        foreach (var prompt in Prompts)
        {
            var button = new Button { Text = prompt, Margin = new Thickness(0, 0, 8, 8) };
            button.Clicked += async (_, _) => await SendAsync(prompt);
            Suggestions.Children.Add(button);
        }

        // Raised on whichever thread changed the view: mid-stream, or inside a tool. Coalesce the changes
        // and render the latest view on the main thread.
        _conversation.Changed += (_, _) =>
        {
            if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
            {
                Dispatcher.Dispatch(Render);
            }
        };
    }

    private void Render()
    {
        Volatile.Write(ref _renderQueued, 0);
        var view = _conversation.View;

        Transcript.ItemsSource = FieldTranscript.From(view);
        SendButton.IsEnabled = !view.IsBusy;
        StopButton.IsEnabled = view.IsBusy;
        Suggestions.IsVisible = view.Messages.Count == 0;
        StatusLabel.Text = FieldStatus.Describe(view);

        // Announce the answer once, when the turn ends, rather than every streamed word.
        if (_lastStatus is ConversationStatus.Running && !view.IsBusy
            && view.Messages.OfType<AssistantMessageView>().LastOrDefault(m => m.Text.Length > 0) is { } answer)
        {
            SemanticScreenReader.Announce(answer.Text);
        }

        _lastStatus = view.Status;
    }

    private async void OnSendClicked(object? sender, EventArgs e) => await SendAsync(Composer.Text);

    private void OnStopClicked(object? sender, EventArgs e) => _conversation.Stop();

    private async Task SendAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || _conversation.View.IsBusy || _conversation.View.OpenInterrupts.Any())
        {
            return;
        }

        Composer.Text = string.Empty;
        await _conversation.SendAsync(text.Trim());
        Composer.Focus();
    }
}
