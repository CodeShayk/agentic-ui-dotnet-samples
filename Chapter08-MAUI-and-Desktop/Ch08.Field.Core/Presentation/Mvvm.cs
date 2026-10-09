using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Ch08.Field.Core.Presentation;

/// <summary>Posts work to the UI thread. Each UI framework supplies its own; tests run it inline.</summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread, later if the caller isn't on it.</summary>
    /// <param name="action">The work to run.</param>
    void Post(Action action);
}

/// <summary>A dispatcher that runs work immediately on the calling thread: for tests and console apps.</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    /// <summary>The shared instance.</summary>
    public static InlineDispatcher Instance { get; } = new();

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}

/// <summary>A minimal <see cref="INotifyPropertyChanged"/> base for view models.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets <paramref name="field"/> and raises <see cref="PropertyChanged"/> if the value changed.</summary>
    /// <returns><see langword="true"/> if the value changed.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>A command that runs a delegate when its predicate allows it.</summary>
/// <param name="execute">What the command does.</param>
/// <param name="canExecute">Whether it can run now; <see langword="null"/> means always.</param>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    /// <summary>Tells bound controls to ask <see cref="CanExecute"/> again.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
