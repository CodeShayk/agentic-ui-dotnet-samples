using Ch08.Field.Core.Presentation;

namespace Ch08.Beacon.Field;

/// <summary>
/// Puts the view model's work on the UI thread. <see cref="MainThread.BeginInvokeOnMainThread"/> runs
/// the action at once when the caller is already on that thread and queues it otherwise, which is what
/// AgentConversation's Changed event needs: it fires on the UI thread when the user sends and on a
/// thread-pool thread while the answer streams.
/// </summary>
public sealed class MauiDispatcher : IUiDispatcher
{
    public void Post(Action action) => MainThread.BeginInvokeOnMainThread(action);
}
