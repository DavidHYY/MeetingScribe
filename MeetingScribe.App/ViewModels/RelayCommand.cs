using System.Windows.Input;

namespace MeetingScribe.App.ViewModels;

/// <summary>
/// Synchronous <see cref="ICommand"/> for simple UI actions. Raises
/// <see cref="CanExecuteChanged"/> only when explicitly told to (<see cref="RaiseCanExecuteChanged"/>)
/// - unlike WPF's <c>RelayCommand</c>, this does not piggyback on
/// <c>System.Windows.Input.CommandManager.RequerySuggested</c> (a WPF-only type with no
/// Avalonia equivalent); every call site that can change a command's enabled state already
/// calls <see cref="RaiseCanExecuteChanged"/> explicitly via <c>RequeryCommands()</c>.
/// </summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    private readonly Func<bool>? _canExecute = canExecute;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
