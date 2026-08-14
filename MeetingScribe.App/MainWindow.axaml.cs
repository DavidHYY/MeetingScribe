using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using MeetingScribe.App.ViewModels;

namespace MeetingScribe.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(this);
        DataContext = _viewModel;

        _viewModel.LiveTranscriptLines.CollectionChanged += OnLiveTranscriptLinesChanged;
        Closing += OnClosing;
    }

    private void OnLiveTranscriptLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Avalonia's ListBox has no direct ScrollIntoView(item) call analogous to WPF's;
        // setting SelectedIndex brings the newly-selected row into view instead
        // (ListBox.AutoScrollToSelectedItem defaults to true), which is the simplest
        // reliable cross-platform way to keep the live transcript pane pinned to the
        // latest line.
        if (e.Action == NotifyCollectionChangedAction.Add && LiveTranscriptListBox.ItemCount > 0)
        {
            LiveTranscriptListBox.SelectedIndex = LiveTranscriptListBox.ItemCount - 1;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed)
        {
            return;
        }

        e.Cancel = true;

        if (_viewModel.IsRecording)
        {
            var confirmed = await _viewModel.ConfirmDiscardIfRecordingAsync(this).ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }
        }

        // Give the recorder/transcriber a clean shutdown (flush WAV files, dispose the
        // native whisper model) instead of letting process exit tear them down mid-write.
        await _viewModel.DisposeAsync().ConfigureAwait(true);
        _closingConfirmed = true;

        // Calling Close() synchronously here - even after the await - risks re-entering
        // this same Closing dispatch on some platforms; posting it as a new,
        // lower-priority dispatcher operation lets any in-flight closing unwind finish
        // first (mirrors the equivalent WPF guard this code had before the Avalonia port,
        // verified necessary there).
        Dispatcher.UIThread.Post(Close, DispatcherPriority.Background);
    }
}
