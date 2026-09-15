using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace TomLabs.AutoUpdate.Avalonia;

/// <summary>
/// Presentation state for the update banner: one line of text, one action, a progress value.
/// Attach it to <see cref="Updater.Current"/> (the default) or any updater instance.
/// </summary>
public sealed class UpdateBannerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Updater? _updater;

    public event PropertyChangedEventHandler? PropertyChanged;

    public UpdateBannerViewModel() : this(Updater.Current) { }

    public UpdateBannerViewModel(Updater? updater)
    {
        _updater = updater;
        PrimaryCommand = new RelayCommand(Primary, () => _updater != null && State is UpdateState.Available or UpdateState.ReadyToInstall or UpdateState.Failed);
        CheckCommand = new RelayCommand(() => _ = _updater?.CheckAsync(), () => _updater != null && State is not (UpdateState.Disabled or UpdateState.Checking or UpdateState.Downloading));
        OpenNotesCommand = new RelayCommand(OpenNotes, () => !string.IsNullOrEmpty(_updater?.Available?.NotesUrl));
        if (_updater != null) _updater.StateChanged += OnStateChanged;
    }

    public UpdateState State => _updater?.State ?? UpdateState.Disabled;

    public bool IsVisible => State is UpdateState.Available or UpdateState.Downloading or UpdateState.ReadyToInstall or UpdateState.Failed;

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;

    public bool IsDownloading => State == UpdateState.Downloading;

    public bool IsFailed => State == UpdateState.Failed;

    public bool IsReady => State == UpdateState.ReadyToInstall;

    /// <summary>0–100 for a progress bar.</summary>
    public double ProgressPercent => (_updater?.Progress ?? 0) * 100;

    public string CurrentVersion => _updater is null ? string.Empty : "v" + _updater.Build.Version;

    public string? AvailableVersion => _updater?.Available is { } u ? "v" + u.Version : null;

    public string? Notes => _updater?.Available?.Notes;

    public string Channel => _updater?.Channel.ToString().ToLowerInvariant() ?? string.Empty;

    /// <summary>The banner text.</summary>
    public string Message => State switch
    {
        UpdateState.Available => $"{AvailableVersion} available",
        UpdateState.Downloading => $"Downloading {AvailableVersion} · {ProgressPercent:0}%",
        UpdateState.ReadyToInstall => $"{AvailableVersion} ready",
        UpdateState.Failed => _updater?.Error ?? "Update failed",
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => $"{CurrentVersion} is up to date",
        _ => string.Empty,
    };

    /// <summary>Label of the one button.</summary>
    public string PrimaryLabel => State switch
    {
        UpdateState.Available => "Update",
        UpdateState.ReadyToInstall => "Restart to update",
        UpdateState.Failed => "Retry",
        _ => string.Empty,
    };

    public ICommand PrimaryCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand OpenNotesCommand { get; }

    private void Primary()
    {
        switch (State)
        {
            case UpdateState.Available:
                _ = _updater!.DownloadAsync();
                break;
            case UpdateState.ReadyToInstall:
                _updater!.ApplyAndRestart();
                break;
            case UpdateState.Failed:
                _ = _updater!.Available != null ? _updater.DownloadAsync() : _updater.CheckAsync();
                break;
        }
    }

    private void OpenNotes()
    {
        var url = _updater?.Available?.NotesUrl;
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser; nothing to do */ }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        foreach (var name in new[] { nameof(State), nameof(IsVisible), nameof(IsBusy), nameof(IsDownloading), nameof(IsFailed), nameof(IsReady),
                     nameof(ProgressPercent), nameof(AvailableVersion), nameof(Notes), nameof(Channel), nameof(Message), nameof(PrimaryLabel) })
            OnPropertyChanged(name);
        ((RelayCommand)PrimaryCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CheckCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenNotesCommand).RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_updater != null) _updater.StateChanged -= OnStateChanged;
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => _canExecute();
        public void Execute(object? parameter) => _execute();
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
