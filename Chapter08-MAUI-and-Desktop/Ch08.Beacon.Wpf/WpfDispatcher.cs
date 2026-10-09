using System.Windows.Threading;
using Ch08.Field.Core.Presentation;

namespace Ch08.Beacon.Wpf;

/// <summary>Runs view-model work on WPF's UI thread: at once when already there, queued otherwise.</summary>
public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
