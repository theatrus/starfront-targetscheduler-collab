using System.Windows;
using System.Windows.Input;

namespace StarfrontCollab.Plugin;

/// An async button: disabled while it runs, errors go to the given handler.
internal sealed class Command(Func<Task> execute, Func<bool>? canExecute, Action<Exception> reportError) : ICommand
{
    private bool running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true;
        Refresh();
        try { await execute(); }
        catch (Exception error) { reportError(error); }
        finally
        {
            running = false;
            Refresh();
        }
    }

    internal void Refresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(Refresh);
            return;
        }
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
