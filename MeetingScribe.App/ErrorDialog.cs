using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeetingScribe.App;

/// <summary>
/// Minimal modal dialog replacing WPF's <c>System.Windows.MessageBox</c>, which has no
/// Avalonia equivalent in core (WPF's is a Win32-only API; Avalonia deliberately ships no
/// built-in message box so as not to force a look-and-feel). Built directly in code
/// rather than as a third .axaml view - it is small and fixed enough that hand-written
/// layout is clearer than a separate markup file for two call sites (crash notice,
/// stop-while-recording confirmation).
/// </summary>
public static class ErrorDialog
{
    /// <summary>Shows a blocking OK-only dialog. Used for crash/error notices.</summary>
    public static async void ShowError(Window owner, string title, string message)
    {
        var dialog = BuildDialog(title, message, out var buttonsPanel);

        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
        ok.Click += (_, _) => dialog.Close();
        buttonsPanel.Children.Add(ok);

        await dialog.ShowDialog(owner).ConfigureAwait(true);
    }

    /// <summary>Shows a blocking Yes/No confirmation dialog. Returns true if the user chose Yes.</summary>
    public static async Task<bool> ShowConfirmAsync(Window owner, string title, string message)
    {
        var dialog = BuildDialog(title, message, out var buttonsPanel);
        var result = false;

        var yes = new Button { Content = "Yes", MinWidth = 80, IsDefault = true };
        yes.Click += (_, _) => { result = true; dialog.Close(); };

        var no = new Button { Content = "No", MinWidth = 80, IsCancel = true, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
        no.Click += (_, _) => { result = false; dialog.Close(); };

        buttonsPanel.Children.Add(yes);
        buttonsPanel.Children.Add(no);

        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }

    private static Window BuildDialog(string title, string message, out StackPanel buttonsPanel)
    {
        buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
        };

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
        };

        var root = new DockPanel { Margin = new Avalonia.Thickness(20) };
        DockPanel.SetDock(buttonsPanel, Dock.Bottom);
        root.Children.Add(buttonsPanel);
        root.Children.Add(text);

        return new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root,
        };
    }
}
