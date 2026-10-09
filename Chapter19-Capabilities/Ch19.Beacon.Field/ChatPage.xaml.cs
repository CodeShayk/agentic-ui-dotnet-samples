using Beacon.Client.Core;
using Ch19.Beacon.Adaptive.Capabilities;
using Ch19.Beacon.Adaptive.Planning;
using Ch19.Beacon.Adaptive.Versioning;

namespace Ch19.Beacon.Field;

/// <summary>One line in Beacon Field's chat. Plain text: the Field never renders model output as markup.</summary>
public sealed record FieldLine(string Id, string Author, string Text);

/// <summary>
/// Beacon Field's chat. When it first appears it reads the agent's capabilities and shapes the composer
/// from the plan: Photo and Voice note buttons, their notes, or the text templates that replace them.
/// </summary>
public partial class ChatPage : ContentPage
{
    private readonly FieldSession _session;
    private readonly IMediaCapture _capture;
    private ComposerModel? _composer;
    private bool _started;
    private int _renderQueued;

    public ChatPage(FieldSession session, IMediaCapture capture)
    {
        InitializeComponent();
        _session = session;
        _capture = capture;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_started)
        {
            _started = true;
            await StartAsync();
        }
    }

    private async Task StartAsync()
    {
        if (_session.Conversation is { } previous)
        {
            previous.Changed -= OnConversationChanged;
        }

        AgentLabel.Text = "Reading the agent's capabilities…";
        await _session.DiscoverAsync();
        _session.Conversation!.Changed += OnConversationChanged;

        _composer = new ComposerModel(_session.Plan, _capture, BeaconFeature.PhotoInput, BeaconFeature.VoiceNote);
        Apply(PhotoButton, _composer.Button(BeaconFeature.PhotoInput));
        Apply(VoiceButton, _composer.Button(BeaconFeature.VoiceNote));
        Render();
    }

    // The button shows what the plan decided; its note doubles as the screen reader's hint.
    private static void Apply(Button button, InputButton model)
    {
        button.IsVisible = model.IsVisible;
        button.Text = model.Text;
        SemanticProperties.SetHint(button, model.Caption ?? string.Empty);
    }

    // Raised on whichever thread changed the view. Coalesce and render on the main thread.
    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e)
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
        {
            Dispatcher.Dispatch(Render);
        }
    }

    private void Render()
    {
        Volatile.Write(ref _renderQueued, 0);
        var view = _session.Conversation?.View ?? ConversationView.Empty;
        var plan = _session.Plan;

        AgentLabel.Text = AgentSummary(view);
        Transcript.ItemsSource = Lines(view, plan);
        SendButton.IsEnabled = !view.IsBusy;
        StopButton.IsEnabled = view.IsBusy;
        NewChatButton.IsEnabled = !view.IsBusy;

        if (_composer is not null)
        {
            NotesLabel.Text = string.Join(Environment.NewLine, _composer.Notes);
            NotesLabel.IsVisible = _composer.Notes.Count > 0;
            var tray = string.Join(", ", _composer.Tray.Items.Select(i => i.FileName));
            TrayLabel.Text = _composer.LastRefusal ?? (tray.Length > 0 ? $"Attached: {tray}" : string.Empty);
            TrayLabel.IsVisible = TrayLabel.Text.Length > 0;
        }
    }

    private string AgentSummary(ConversationView view)
    {
        var profile = _session.Plan.Profile;
        var who = _session.Discovery?.Outcome == DiscoveryOutcome.Declared
            ? $"{profile.Name} {profile.Version}" + (profile.DeploymentProfile is { } p ? $" ({p})" : string.Empty)
            : "An agent that doesn't describe itself";
        if (view.CurrentRun is not { } run)
        {
            return who;
        }

        var check = ProducerVersionCheck.From(run.ProtocolVersion);
        return check.Verdict == VersionVerdict.Same ? $"{who} · AG-UI {check.Declared}" : $"{who} · {check.Summary}";
    }

    private static List<FieldLine> Lines(ConversationView view, FeaturePlan plan)
    {
        var showReasoning = plan.ShowsReasoning(view.Messages.OfType<ReasoningMessageView>().Any());
        List<FieldLine> lines = [];
        foreach (var message in view.Messages)
        {
            switch (message)
            {
                case UserMessageView user:
                    var attached = user.Attachments.Count == 0 ? string.Empty : $" ({user.Attachments.Count} attached)";
                    lines.Add(new FieldLine(user.Id, "You", user.Text + attached));
                    break;
                case ReasoningMessageView reasoning when showReasoning && reasoning.Text.Length > 0:
                    lines.Add(new FieldLine(reasoning.Id, "Beacon's reasoning", reasoning.Text));
                    break;
                case AssistantMessageView { Text.Length: > 0 } assistant:
                    lines.Add(new FieldLine(assistant.Id, "Beacon", assistant.IsPartial ? assistant.Text + " (stopped)" : assistant.Text));
                    break;
            }
        }

        return lines;
    }

    private Task OnInputAsync(BeaconFeature feature) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        if (_composer is null)
        {
            return;
        }

        _composer.Draft = Composer.Text ?? string.Empty;
        await _composer.PressAsync(feature);
        Composer.Text = _composer.Draft;
        Render();
    });

    private async void OnPhotoClicked(object? sender, EventArgs e) => await OnInputAsync(BeaconFeature.PhotoInput);

    private async void OnVoiceClicked(object? sender, EventArgs e) => await OnInputAsync(BeaconFeature.VoiceNote);

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        if (_composer is null || _session.Conversation is not { View.IsBusy: false } conversation)
        {
            return;
        }

        _composer.Draft = Composer.Text ?? string.Empty;
        if (_composer.TakeMessage() is not { } message)
        {
            return;
        }

        Composer.Text = string.Empty;
        await conversation.SendAsync(message);
        Composer.Focus();
    }

    private void OnStopClicked(object? sender, EventArgs e) => _session.Conversation?.Stop();

    // A new chat reads the capabilities again, so a redeployed agent is noticed here.
    private async void OnNewChatClicked(object? sender, EventArgs e) => await StartAsync();
}
