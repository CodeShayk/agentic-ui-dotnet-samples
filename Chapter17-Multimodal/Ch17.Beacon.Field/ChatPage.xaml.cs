using Beacon.Client.Core;
using Ch17.Field.Capture;
using Ch17.Media;
using Microsoft.Extensions.AI;

namespace Ch17.Beacon.Field;

/// <summary>
/// Beacon Field's chat with attachments. The capture flow (camera, recorder, file picker) and the checks
/// live in Ch17.Field.Capture; this page renders the conversation and the tray and reports what happened.
/// </summary>
public partial class ChatPage : ContentPage
{
    private readonly AgentConversation _conversation;
    private readonly FieldCapture _capture;
    private readonly AttachmentTray _tray;
    private int _renderQueued;
    private ConversationStatus _lastStatus;
    private string? _note;

    public ChatPage(AgentConversation conversation, FieldCapture capture, AttachmentTray tray)
    {
        InitializeComponent();
        (_conversation, _capture, _tray) = (conversation, capture, tray);

        // Raised on whichever thread changed the view. Coalesce and render the latest on the main thread.
        _conversation.Changed += (_, _) => QueueRender();
        _tray.Changed += (_, _) => QueueRender();
        Render();
    }

    private void QueueRender()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
        {
            Dispatcher.Dispatch(Render);
        }
    }

    private void Render()
    {
        Volatile.Write(ref _renderQueued, 0);
        var view = _conversation.View;

        Transcript.ItemsSource = FieldTranscript.From(view);
        Tray.ItemsSource = _tray.Items.Select(i => new TrayLine(i, i.Name ?? "attachment", $"{i.Name} · {i.MediaType} · {ByteSize.Format(i.Data.Length)}")).ToList();
        BudgetLabel.Text = $"This message: {ByteSize.Format(_tray.PendingBytes)}. Conversation: " +
            $"{ByteSize.Format(ConversationBytes(view) + _tray.PendingBytes)} of {ByteSize.Format(MediaPolicy.Beacon.MaxBytesPerRequest)}.";

        SendButton.IsEnabled = !view.IsBusy && !_capture.IsRecording;
        StopButton.IsEnabled = view.IsBusy;
        RecordButton.Text = _capture.IsRecording ? "Stop recording" : "Record";
        RecordButton.IsEnabled = _capture.CanRecord;
        StatusLabel.Text = _note ?? FieldStatus.Describe(view);

        // Announce the answer once, when the turn ends, rather than every streamed word.
        if (_lastStatus is ConversationStatus.Running && !view.IsBusy
            && view.Messages.OfType<AssistantMessageView>().LastOrDefault(m => m.Text.Length > 0) is { } answer)
        {
            SemanticScreenReader.Announce(answer.Text);
        }

        _lastStatus = view.Status;
    }

    private static long ConversationBytes(ConversationView view) =>
        MediaAttachment.BytesIn(view.Messages.OfType<UserMessageView>().SelectMany(u => u.Attachments));

    private async void OnPhotoClicked(object? sender, EventArgs e) => await ReportAsync(_capture.AddPhotoAsync);

    private async void OnRecordClicked(object? sender, EventArgs e) => await ReportAsync(_capture.ToggleRecordingAsync);

    private async void OnFileClicked(object? sender, EventArgs e) => await ReportAsync(_capture.AddFileAsync);

    // Every capture ends in a sentence for the status line and the screen reader, including failures.
    private async Task ReportAsync(Func<CancellationToken, Task<string>> action)
    {
        try
        {
            _note = await action(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _note = "That didn't work on this device. Use Attach file instead.";
        }

        SemanticScreenReader.Announce(_note);
        Render();
    }

    private void OnRemoveClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: DataContent item })
        {
            _tray.Remove(item);
            _note = $"{item.Name} removed.";
        }
    }

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        if (_conversation.View.IsBusy || _capture.IsRecording)
        {
            return;
        }

        var (message, problem) = _tray.TakeMessage(Composer.Text, ConversationBytes(_conversation.View));
        if (problem is not null)
        {
            _note = problem;
            SemanticScreenReader.Announce(problem);
            Render();
            return;
        }

        if (message is null)
        {
            return;
        }

        _note = null;
        Composer.Text = string.Empty;
        await _conversation.SendAsync(message);
        Composer.Focus();
    }

    private void OnStopClicked(object? sender, EventArgs e) => _conversation.Stop();
}
