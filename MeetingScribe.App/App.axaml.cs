using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace MeetingScribe.App;

/// <summary>
/// Application entry point. Installs a UI-thread unhandled-exception handler (Avalonia's
/// equivalent of WPF's <c>DispatcherUnhandledException</c>) so an unexpected exception
/// from a command handler surfaces to the user and the crash log instead of silently
/// tearing down the process - a recording in progress should not be lost to e.g. a
/// rendering glitch in a settings dialog.
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnUiThreadUnhandledException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnUiThreadUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Program.LogCrash("UI thread", e.Exception);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            ErrorDialog.ShowError(
                owner,
                "MeetingScribe - Unexpected Error",
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nDetails were written to:\n{Program.LogDirectory}\\crash.log");
        }

        // Keep the app alive for a UI-thread exception where reasonably possible - a
        // recording in progress should not be silently torn down by e.g. a rendering
        // glitch in a settings dialog.
        e.Handled = true;
    }
}
