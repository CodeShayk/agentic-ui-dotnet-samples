using Beacon.Client.Core;
using Ch15.Forms;

namespace Ch15.Beacon.Field;

/// <summary>
/// Beacon Field's chat. It renders the conversation's view state, and while the agent waits on input
/// interrupts it shows a native form per question and sends Sam's answers together.
/// </summary>
public partial class ChatPage : ContentPage
{
    private const string Prompt =
        "Is the heat pump on WO-1003 still under warranty? Its outdoor fan is grinding, and I need to know whether to replace the motor or just the bearings.";

    private readonly AgentConversation _conversation;
    private readonly TimeProvider _clock;
    private readonly List<QuestionFormView> _questions = [];
    private readonly IDispatcherTimer _expiryTimer;
    private int _renderQueued;
    private ConversationStatus _lastStatus;

    public ChatPage(AgentConversation conversation, TimeProvider clock)
    {
        InitializeComponent();
        (_conversation, _clock) = (conversation, clock);
        Composer.Text = Prompt;

        // Raised on whichever thread changed the view; coalesce and render the latest view on the main thread.
        _conversation.Changed += (_, _) =>
        {
            if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
            {
                Dispatcher.Dispatch(Render);
            }
        };

        // Expiry is a property of time, not of events: re-check the open questions every few seconds.
        _expiryTimer = Dispatcher.CreateTimer();
        _expiryTimer.Interval = TimeSpan.FromSeconds(5);
        _expiryTimer.Tick += (_, _) => RefreshQuestions();
    }

    private void Render()
    {
        Volatile.Write(ref _renderQueued, 0);
        var view = _conversation.View;

        Transcript.ItemsSource = FieldTranscript.From(view);
        SyncQuestions(view);

        var waiting = _questions.Count > 0;
        SendButton.IsEnabled = Composer.IsEnabled = !view.IsBusy && !waiting;
        StopButton.IsEnabled = view.IsBusy;
        StatusLabel.Text = FieldStatus.Describe(view);

        // Announce the answer once, when the turn ends, rather than every streamed word.
        if (_lastStatus is ConversationStatus.Running && !view.IsBusy && !waiting
            && view.Messages.OfType<AssistantMessageView>().LastOrDefault(m => m.Text.Length > 0) is { } answer)
        {
            SemanticScreenReader.Announce(answer.Text);
        }

        _lastStatus = view.Status;
    }

    // One form per open input interrupt. Forms for questions that are still open are kept, with what was typed.
    private void SyncQuestions(ConversationView view)
    {
        List<InterruptView> open = view.IsBusy ? [] : [.. view.OpenInterrupts.Where(i => i.Kind == InterruptKind.Input)];

        foreach (var gone in _questions.Where(q => !open.Exists(i => i.Id == q.Question.Id)).ToList())
        {
            _questions.Remove(gone);
            QuestionsHost.Remove(gone);
        }

        var arrived = open.Where(i => !_questions.Exists(q => q.Question.Id == i.Id)).ToList();
        foreach (var interrupt in arrived)
        {
            var form = new QuestionFormView(new FormQuestion(interrupt.Id, interrupt.Message, interrupt.ResponseSchema, interrupt.ExpiresAt, interrupt.Metadata));
            _questions.Add(form);
            QuestionsHost.Add(form);
        }

        QuestionsPanel.IsVisible = _questions.Count > 0;
        QuestionsHeading.Text = _questions.Count == 1 ? "Beacon needs an answer" : $"Beacon needs {_questions.Count} answers";
        RefreshQuestions();

        if (arrived.Count > 0)
        {
            AnswersError.IsVisible = false;
            _expiryTimer.Start();
            SemanticScreenReader.Announce(QuestionsHeading.Text);
        }
        else if (_questions.Count == 0)
        {
            _expiryTimer.Stop();
        }
    }

    private void RefreshQuestions()
    {
        var now = _clock.GetUtcNow();
        foreach (var form in _questions)
        {
            form.Refresh(now);
        }

        SendAnswersButton.Text = _questions.All(q => q.Question.CanOnlySkip(now)) ? "Skip and carry on" : "Send answers";
    }

    private async void OnSendAnswersClicked(object? sender, EventArgs e)
    {
        if (!FormQuestion.TryDecide(_questions.Select(q => q.Question).ToList(), _clock.GetUtcNow(), out var decided, out var invalid))
        {
            _questions.ForEach(q => q.ShowAllErrors());
            AnswersError.Text = "Some answers need fixing before Beacon can use them.";
            AnswersError.IsVisible = true;
            _questions.First(q => q.Question == invalid).FocusFirstInvalid();
            return;
        }

        SendAnswersButton.IsEnabled = false;
        try
        {
            await FieldAnswers.SendAsync(_conversation, decided);
        }
        catch (ArgumentException)
        {
            // A question expired between the tap and the send; nothing was sent, and the form now says so.
            RefreshQuestions();
        }
        finally
        {
            SendAnswersButton.IsEnabled = true;
        }
    }

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        var text = Composer.Text;
        if (string.IsNullOrWhiteSpace(text) || _conversation.View.IsBusy || _conversation.View.OpenInterrupts.Any())
        {
            return;
        }

        Composer.Text = string.Empty;
        await _conversation.SendAsync(text.Trim());
    }

    private void OnStopClicked(object? sender, EventArgs e) => _conversation.Stop();
}
