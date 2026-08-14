using System.Windows.Input;

namespace MeetingScribe.App.ViewModels;

/// <summary>
/// Async <see cref="ICommand"/> for Start/Stop-style actions: disables itself while a
/// previous invocation is still running (re-entrancy guard) rather than letting a
/// double-click fire the handler twice. See <see cref="RelayCommand"/> for why
/// <see cref="CanExecuteChanged"/> is a plain event here rather than routed through
/// <c>CommandManager.RequerySuggested</c> (WPF-only, no Avalonia equivalent).
/// </summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Func<Task> _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    private readonly Func<bool>? _canExecute = canExecute;
    private bool _isExecuting;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
