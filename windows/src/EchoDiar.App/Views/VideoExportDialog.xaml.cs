using System.Diagnostics;
using EchoDiar.Platform.Screen;
using EchoDiar.Platform.SystemServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace EchoDiar.App.Views;

/// <summary>
/// Exports a conversation's screen recording with its audio to an mp4 the user picks (macOS VideoExportSheet): the
/// audio is looked up off the UI thread, then Export… opens the save dialog, the export runs with its progress and
/// Cancel, and Reveal in Explorer shows the file at the end. Nothing is written inside the conversation's folder.
/// Closing the dialog cancels the export, which removes its partial file.
/// </summary>
public sealed partial class VideoExportDialog : ContentDialog
{
    private enum Phase { Choosing, Exporting, Done, Failed }

    private readonly string _title;
    private readonly string _folder;
    private readonly CancellationTokenSource _cancel = new();
    private Phase _phase = Phase.Choosing;
    private string? _video;
    private VideoExportAudio? _audio;

    /// <summary>The audio and the video have been looked up; until then nothing can be picked.</summary>
    private bool _resolved;

    private string? _destination;

    public VideoExportDialog(string title, string folder)
    {
        _title = title;
        _folder = folder;
        InitializeComponent();
        Title = Loc.T("Export Video");
        AudioChoices.SelectionChanged += (_, _) => Update();
        PrimaryButtonClick += (sender, e) =>
        {
            // Export… stays in the dialog: the save dialog, then the progress.
            e.Cancel = true;
            if (_phase == Phase.Choosing) _ = ChooseFileAsync();
        };
        SecondaryButtonClick += (sender, e) =>
        {
            e.Cancel = true;
            if (_destination is not { } path) return;
            try
            {
                ShellFiles.Reveal(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"EchoDiar: could not reveal {path}: {ex}");
            }
        };
        // Cancel, Done, Close or Esc: an export still running stops and removes its file.
        Closed += (_, _) => _cancel.Cancel();
        Update();
        _ = ResolveAsync();
    }

    private async Task ResolveAsync()
    {
        var folder = _folder;
        (_video, _audio) = await Task.Run(() => (ConversationView.VideoPath(folder), VideoExportAudio.Find(folder)));
        _resolved = true;
        if (_video is null)
        {
            Fail();
            return;
        }
        // The first the conversation has, as macOS: Both, then the system audio, the microphone, no audio.
        RadioButton[] order = [BothChoice, SystemChoice, MicChoice, NoneChoice];
        var first = order.First(choice => Offers(Channels(choice)));
        foreach (var choice in order) choice.IsEnabled = Offers(Channels(choice));
        AudioChoices.SelectedItem = first;
        AudioChoices.IsEnabled = true;
        Update();
    }

    private VideoExportChannels Channels(RadioButton choice) =>
        choice == BothChoice ? VideoExportChannels.Both
        : choice == MicChoice ? VideoExportChannels.Mic
        : choice == SystemChoice ? VideoExportChannels.System
        : VideoExportChannels.None;

    private VideoExportChannels? Choice => AudioChoices.SelectedItem is RadioButton choice ? Channels(choice) : null;

    private bool Offers(VideoExportChannels channels) =>
        channels == VideoExportChannels.None || _audio?.Sources(channels) is not null;

    /// <summary>The panel and the buttons of the phase (macOS <c>buttons</c>).</summary>
    private void Update()
    {
        AudioChoices.Visibility = Shown(_phase == Phase.Choosing);
        ProgressPanel.Visibility = Shown(_phase == Phase.Exporting);
        DonePanel.Visibility = Shown(_phase == Phase.Done);
        FailureText.Visibility = Shown(_phase == Phase.Failed);
        PrimaryButtonText = _phase == Phase.Choosing ? Loc.T("Export…") : "";
        IsPrimaryButtonEnabled = _phase == Phase.Choosing && _resolved && Choice is { } choice && Offers(choice);
        SecondaryButtonText = _phase == Phase.Done ? Loc.T("Reveal in Explorer") : "";
        CloseButtonText = _phase switch
        {
            Phase.Done => Loc.T("Done"),
            Phase.Failed => Loc.T("Close"),
            _ => Loc.T("Cancel"),
        };
        DefaultButton = _phase switch
        {
            Phase.Choosing => ContentDialogButton.Primary,
            Phase.Done => ContentDialogButton.Close,
            _ => ContentDialogButton.None,
        };
    }

    /// <summary>The conversation's title without the characters a file name cannot hold, or "Recording".</summary>
    private string DefaultFileName()
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string([.. _title.Select(c => invalid.Contains(c) ? '-' : c)]).Trim().TrimEnd('.');
        return name.Length == 0 ? Loc.T("Recording") : name;
    }

    private async Task ChooseFileAsync()
    {
        if (XamlRoot?.ContentIslandEnvironment is not { } environment || Choice is not { } choice) return;
        var picker = new FileSavePicker(environment.AppWindowId)
        {
            SuggestedFileName = DefaultFileName(),
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
        };
        picker.FileTypeChoices.Add(Loc.T("MP4 video"), [".mp4"]);
        string? path;
        try
        {
            path = (await picker.PickSaveFileAsync())?.Path;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: the save dialog failed: {e}");
            return;
        }
        if (path is null || _phase != Phase.Choosing || _cancel.IsCancellationRequested) return;
        // Never over the recording or its audio: nothing is written inside the conversation's folder.
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_folder)) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            Fail(Loc.T("Choose a place outside the conversation's folder."));
            return;
        }
        await ExportAsync(path, choice);
    }

    private async Task ExportAsync(string destination, VideoExportChannels choice)
    {
        if (_video is not { } video) return;
        _phase = Phase.Exporting;
        ExportProgress.Value = 0;
        Update();
        var dispatcher = DispatcherQueue;
        try
        {
            await VideoExporter.ExportAsync(video, _audio, choice, destination,
                done => dispatcher.TryEnqueue(() => ExportProgress.Value = done), _cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: video export failed: {e}");
            if (!_cancel.IsCancellationRequested) Fail();
            return;
        }
        _destination = destination;
        DoneText.Text = Path.GetFileName(destination);
        _phase = Phase.Done;
        Update();
    }

    private void Fail(string? message = null)
    {
        FailureText.Text = message ?? Loc.T("The video could not be exported.");
        _phase = Phase.Failed;
        Update();
    }

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
