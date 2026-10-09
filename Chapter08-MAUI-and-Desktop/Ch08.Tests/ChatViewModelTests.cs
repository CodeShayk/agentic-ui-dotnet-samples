using System.Collections.Specialized;
using System.ComponentModel;
using System.Net;
using Beacon.Client.Core;
using Beacon.Domain;
using Ch08.Field.Core;
using Ch08.Field.Core.Connection;
using Ch08.Field.Core.Presentation;
using Ch08.Field.Core.Recovery;
using Microsoft.Extensions.Time.Testing;

namespace Ch08.Tests;

/// <summary>The native clients' view model, driven by Chapter 7's reducer over real and scripted runs.</summary>
public sealed class ChatViewModelTests
{
    private static readonly FieldClientOptions Options = new() { Connection = ConnectionPolicy.Default with { BaseDelay = TimeSpan.Zero } };

    [Fact]
    public async Task The_transcript_is_projected_from_the_view_and_updated_in_place()
    {
        using var field = FieldClient.Create(new InProcessAgentTransport(InProcessAgent.Create(new InMemoryFieldServiceStore())), Options);
        using var chat = new ChatViewModel(field.Conversation, InlineDispatcher.Instance);
        List<NotifyCollectionChangedAction> collectionChanges = [];
        chat.Messages.CollectionChanged += (_, e) => collectionChanges.Add(e.Action);
        List<string> announced = [];
        chat.Announced += (_, text) => announced.Add(text);

        chat.Draft = "What is the status of WO-1001?";
        Assert.True(chat.SendCommand.CanExecute(null));
        await chat.SendAsync(chat.Draft);

        Assert.Equal(3, chat.Messages.Count);
        Assert.Equal((MessageAuthor.User, "What is the status of WO-1001?"), (chat.Messages[0].Author, chat.Messages[0].Text));
        Assert.Equal(("Looked up WO-1001.", false), (chat.Messages[1].Activity, chat.Messages[1].HasText));
        Assert.StartsWith("**WO-1001** is Scheduled", chat.Messages[2].Text);
        Assert.Equal(InlineStyle.Bold, chat.Messages[2].Runs[0].Style); // Markdown, rendered as native runs

        // Items were added, never rebuilt: streaming updated the answer's item in place.
        Assert.All(collectionChanges, action => Assert.Equal(NotifyCollectionChangedAction.Add, action));
        Assert.Equal(string.Empty, chat.Draft);
        Assert.Equal(("Ready", false, RecoveryKind.None), (chat.StatusText, chat.IsBusy, chat.Recovery.Kind));
        Assert.StartsWith("Beacon: WO-1001 is Scheduled", Assert.Single(announced));
    }

    [Fact]
    public async Task Streaming_text_updates_one_item_many_times()
    {
        using var field = FieldClient.Create(new InProcessAgentTransport(InProcessAgent.Create(new InMemoryFieldServiceStore())), Options);
        using var chat = new ChatViewModel(field.Conversation, InlineDispatcher.Instance);
        var textChanges = 0;
        chat.Messages.CollectionChanged += (_, e) =>
        {
            foreach (MessageItem item in e.NewItems ?? Array.Empty<MessageItem>())
            {
                item.PropertyChanged += (_, p) => textChanges += p.PropertyName == nameof(MessageItem.Text) ? 1 : 0;
            }
        };

        await chat.SendAsync("Why is EQ-501 short-cycling?");

        var answer = chat.Messages.Last();
        Assert.StartsWith("The most likely cause is a failing run capacitor", answer.Text);
        Assert.True(textChanges > 5, $"expected many in-place updates, saw {textChanges}");
    }

    [Fact]
    public async Task Going_to_the_background_stops_a_long_turn_after_the_grace_period_and_resuming_resends_nothing()
    {
        var holding = new TaskCompletionSource();
        var agent = new ScriptedTransport(Attempt.Holds("Checking WO-1001", holding));
        using var field = FieldClient.Create(agent, Options);
        var time = new FakeTimeProvider();
        using var chat = new ChatViewModel(field.Conversation, InlineDispatcher.Instance,
            new ChatViewModelOptions { BackgroundGracePeriod = TimeSpan.FromSeconds(15), TimeProvider = time });
        List<string> announced = [];
        chat.Announced += (_, text) => announced.Add(text);

        var turn = chat.SendAsync("What is the status of WO-1001?");
        await holding.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(chat.IsBusy);

        chat.OnBackgrounded();
        time.Advance(TimeSpan.FromSeconds(14));
        Assert.True(chat.IsBusy); // still inside the grace period

        time.Advance(TimeSpan.FromSeconds(1));
        await turn.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConversationStatus.Stopped, field.Conversation.View.Status);
        Assert.Equal((RecoveryKind.CheckFirst, "Check WO-1001"), (chat.Recovery.Kind, chat.Recovery.ActionLabel));
        Assert.StartsWith("Beacon Field went to the background before Beacon finished, and it may have done more than you saw.", chat.Recovery.Message);
        Assert.Empty(announced); // nobody is looking yet

        chat.OnResumed();

        Assert.Single(agent.Inputs); // nothing was sent again
        Assert.Equal(chat.Recovery.Message, Assert.Single(announced));
        Assert.True(chat.Messages.Last().IsPartial);
    }

    [Fact]
    public async Task On_a_desktop_the_background_never_stops_a_turn()
    {
        var holding = new TaskCompletionSource();
        var agent = new ScriptedTransport(Attempt.Holds("Checking WO-1001", holding));
        using var field = FieldClient.Create(agent, Options);
        var time = new FakeTimeProvider();
        using var chat = new ChatViewModel(field.Conversation, InlineDispatcher.Instance, new ChatViewModelOptions { TimeProvider = time });

        var turn = chat.SendAsync("What is the status of WO-1001?");
        await holding.Task.WaitAsync(TestContext.Current.CancellationToken);

        chat.OnBackgrounded();
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(chat.IsBusy);

        chat.StopCommand.Execute(null);
        await turn.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RecoveryKind.None, chat.Recovery.Kind); // the user stopped a lookup: nothing to warn about
    }

    [Fact]
    public async Task Retries_show_in_the_status_line_and_the_first_one_is_announced()
    {
        var store = new InMemoryFieldServiceStore();
        var network = new FlakyNetwork(new InProcessAgentTransport(InProcessAgent.Create(store))) { RefuseFirst = 2 };
        using var field = FieldClient.Create(network, Options);
        using var chat = new ChatViewModel(field.Conversation, InlineDispatcher.Instance, new ChatViewModelOptions { Transport = field.Transport });
        var statuses = StatusTexts(chat);
        List<string> announced = [];
        chat.Announced += (_, text) => announced.Add(text);

        await chat.SendAsync("What is the status of WO-1001?");

        Assert.Contains("No connection yet. Trying again (attempt 2 of 3)…", statuses);
        Assert.Contains("No connection yet. Trying again (attempt 3 of 3)…", statuses);
        Assert.Equal("Ready", chat.StatusText);

        // A screen reader hears the first retry once, then the answer.
        Assert.Equal(2, announced.Count);
        Assert.Equal("No connection yet. Trying again.", announced[0]);
        Assert.StartsWith("Beacon: WO-1001 is Scheduled", announced[1]);
    }

    [Fact]
    public async Task A_refusal_reads_as_busy_rather_than_as_no_connection()
    {
        var agent = new ScriptedTransport(Attempt.Fails(Attempt.Status(HttpStatusCode.ServiceUnavailable)), Attempt.Answers("Ready."));
        using var field = FieldClient.Create(agent, Options);
        using var chat = new ChatViewModel(field.Conversation, InlineDispatcher.Instance, new ChatViewModelOptions { Transport = field.Transport });
        var statuses = StatusTexts(chat);

        await chat.SendAsync("What is the status of WO-1001?");

        Assert.Contains("Beacon is busy. Trying again (attempt 2 of 3)…", statuses);
    }

    [Fact]
    public async Task Connectivity_changes_from_another_thread_reach_the_view_model_through_the_dispatcher()
    {
        var ui = new QueuedDispatcher();
        using var field = FieldClient.Create(new ScriptedTransport(), Options);
        using var chat = new ChatViewModel(field.Conversation, ui) { Draft = "Order CAP-45-5 for WO-1001." };

        // MAUI may raise ConnectivityChanged on a background thread.
        await Task.Run(() => chat.SetOnline(false), TestContext.Current.CancellationToken);

        Assert.True(chat.IsOnline); // nothing changed off the UI thread
        ui.RunPending();
        Assert.False(chat.IsOnline);
        Assert.False(chat.SendCommand.CanExecute(null));
    }

    private static List<string> StatusTexts(ChatViewModel chat)
    {
        List<string> statuses = [];
        ((INotifyPropertyChanged)chat).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.StatusText))
            {
                statuses.Add(chat.StatusText);
            }
        };

        return statuses;
    }

    /// <summary>A UI thread that runs posted work only when the test says so.</summary>
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _pending = new();

        public void Post(Action action) => _pending.Enqueue(action);

        public void RunPending()
        {
            while (_pending.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    [Fact]
    public void Offline_disables_send_and_keeps_the_draft()
    {
        using var field = FieldClient.Create(new ScriptedTransport(), Options);
        using var chat = new ChatViewModel(field.Conversation, InlineDispatcher.Instance) { Draft = "Order CAP-45-5 for WO-1001." };

        chat.SetOnline(false);

        Assert.False(chat.SendCommand.CanExecute(null));
        Assert.Equal(("Offline. Your draft is kept.", "Order CAP-45-5 for WO-1001."), (chat.StatusText, chat.Draft));

        chat.SetOnline(true);
        Assert.True(chat.SendCommand.CanExecute(null));
    }
}
