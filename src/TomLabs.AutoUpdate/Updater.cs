using System.Net.Http.Headers;

namespace TomLabs.AutoUpdate;

/// <summary>
/// Orchestrates check → download → apply for one application. Create it once at startup with
/// <see cref="Start"/>; UI binds to <see cref="State"/>, <see cref="Available"/> and <see cref="Progress"/>
/// and calls <see cref="CheckAsync"/>, <see cref="DownloadAsync"/> and <see cref="ApplyAndRestart"/>.
/// <see cref="StateChanged"/> is raised on the thread that called <see cref="Start"/> when it had a
/// synchronization context (the UI thread), otherwise on a thread-pool thread.
/// </summary>
public sealed class Updater : IDisposable
{
    private readonly UpdateOptions _options;
    private readonly HttpClient _http;
    private readonly SynchronizationContext? _context;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateChannel _channel;
    private string? _downloadedExecutable;

    public static Updater? Current { get; private set; }

    public AppBuildInfo Build { get; }
    public UpdateState State { get; private set; }
    public UpdateInfo? Available { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastCheck { get; private set; }

    /// <summary>Why in-place updating is unavailable on this machine (read-only folder, dotnet run, …), or null.</summary>
    public string? DisabledReason { get; private set; }

    public UpdateChannel Channel
    {
        get => _channel;
        set
        {
            if (_channel == value) return;
            _channel = value;
            _options.ChannelChanged?.Invoke(value);
            Available = null;
            _downloadedExecutable = null;
            SetState(UpdateState.Idle);
            _ = CheckAsync();
        }
    }

    public event EventHandler? StateChanged;

    private Updater(UpdateOptions options)
    {
        _options = options;
        Build = options.Build ?? AppBuildInfo.FromEntryAssembly();
        _channel = options.Channel ?? Build.DefaultChannel;
        _context = SynchronizationContext.Current;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var agent = options.UserAgent ?? $"{options.AppName}/{Build.Version} TomLabs.AutoUpdate";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(agent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Creates the singleton, removes the backup of a previous update and schedules the startup and periodic checks.
    /// Call once from the UI thread after the framework is initialised.
    /// </summary>
    public static Updater Start(UpdateOptions options)
    {
        var updater = new Updater(options);
        Current = updater;
        updater.Log($"Build {updater.Build.Version} ({updater.Build.ShortCommit ?? "no commit"}), channel {updater._channel}, exe {updater.Build.ExecutablePath}");

        updater.DisabledReason = UpdateApplier.CheckCanApply(updater.Build.ExecutablePath);
        if (updater.DisabledReason != null)
        {
            updater.Log($"In-place update unavailable: {updater.DisabledReason}");
            updater.SetState(UpdateState.Disabled);
        }
        else
        {
            updater.SetState(UpdateState.Idle);
            _ = updater.RunBackgroundAsync();
        }

        return updater;
    }

    private async Task RunBackgroundAsync()
    {
        var token = _lifetime.Token;
        try
        {
            await UpdateApplier.CleanUpAsync(Build.ExecutablePath, token).ConfigureAwait(false);
            UpdateDownloader.Prune(DownloadDirectory, keepVersion: null);

            if (_options.CheckOnStartup)
            {
                await Task.Delay(_options.StartupDelay, token).ConfigureAwait(false);
                await CheckAsync(token).ConfigureAwait(false);
            }

            while (_options.CheckInterval > TimeSpan.Zero)
            {
                await Task.Delay(_options.CheckInterval, token).ConfigureAwait(false);
                if (State is UpdateState.ReadyToInstall or UpdateState.Downloading) continue;
                await CheckAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    /// <summary>Looks for a newer build on the current channel. Never throws; failures land in <see cref="Error"/>.</summary>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (State == UpdateState.Disabled) return false;
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Available != null;

        try
        {
            SetState(UpdateState.Checking);
            var manifest = await _options.Source.FetchAsync(_channel, _http, cancellationToken).ConfigureAwait(false);
            LastCheck = DateTimeOffset.Now;

            var update = manifest is null ? null : Evaluate(manifest);
            if (update is null)
            {
                Available = null;
                SetState(UpdateState.UpToDate);
                return false;
            }

            Available = update;
            Log($"Update available: {update.Version} ({update.ShortCommit})");
            SetState(UpdateState.Available);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail("Update check failed", ex);
            return false;
        }
        finally
        {
            _gate.Release();
        }

        if (_options.DownloadAutomatically)
            await DownloadAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private UpdateInfo? Evaluate(UpdateManifest manifest)
    {
        if (!string.IsNullOrEmpty(manifest.Name) && !string.Equals(manifest.Name, _options.AppName, StringComparison.OrdinalIgnoreCase))
        {
            Log($"Manifest is for '{manifest.Name}', not '{_options.AppName}'; ignored.");
            return null;
        }

        if (!SemVersion.TryParse(manifest.Version, out var version))
        {
            Log($"Manifest version '{manifest.Version}' is not a semantic version; ignored.");
            return null;
        }

        var newer = _channel == UpdateChannel.Nightly
            // Nightly builds of the same version differ only by commit; a different commit is a newer build.
            ? version > Build.Version || (version.Equals(Build.Version) && !SameCommit(manifest.Commit, Build.Commit))
            : version > Build.Version;
        if (!newer) return null;

        var rid = RuntimeRid.Current;
        var asset = manifest.Assets.FirstOrDefault(a => string.Equals(a.Rid, rid, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            Log($"Manifest has no asset for {rid}; ignored.");
            return null;
        }

        var url = _options.Source.ResolveAssetUrl(manifest, asset);
        return new UpdateInfo(version, manifest.Commit, manifest.PublishedAt, manifest.Notes, manifest.NotesUrl,
            url.ToString(), asset.File, asset.Sha256, asset.Size, asset.Executable);
    }

    private static bool SameCommit(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var n = Math.Min(a.Length, b.Length);
        return n >= 7 && string.Equals(a[..n], b[..n], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Downloads and verifies the available update. Never throws.</summary>
    public async Task<bool> DownloadAsync(CancellationToken cancellationToken = default)
    {
        var update = Available;
        if (update is null || State == UpdateState.Disabled) return false;
        if (State == UpdateState.ReadyToInstall && _downloadedExecutable != null) return true;
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;

        try
        {
            Progress = 0;
            SetState(UpdateState.Downloading);
            var progress = new Progress<double>(p => { Progress = p; Raise(); });
            var exeName = Path.GetFileName(Build.ExecutablePath);
            _downloadedExecutable = await UpdateDownloader.DownloadAsync(update, DownloadDirectory, exeName,
                _options.RequireChecksum, _http, progress, cancellationToken).ConfigureAwait(false);
            UpdateDownloader.Prune(DownloadDirectory, keepVersion: update.Version.ToString());
            Progress = 1;
            SetState(UpdateState.ReadyToInstall);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _downloadedExecutable = null;
            Fail("Download failed", ex);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Swaps the executable, starts the new build and exits this process. Returns false (with <see cref="Error"/> set) if the swap failed.</summary>
    public bool ApplyAndRestart()
    {
        if (State != UpdateState.ReadyToInstall || _downloadedExecutable is null) return false;
        try
        {
            Log($"Applying {Available?.Version} from {_downloadedExecutable}");
            UpdateApplier.Apply(Build.ExecutablePath, _downloadedExecutable, _options.RelaunchArguments);
            (_options.ExitApplication ?? (() => Environment.Exit(0)))();
            return true;
        }
        catch (Exception ex)
        {
            Fail("Installing the update failed", ex);
            return false;
        }
    }

    private string DownloadDirectory => _options.DownloadDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _options.AppName, "updates");

    private void Fail(string what, Exception ex)
    {
        Error = $"{what}: {ex.Message}";
        Log(Error);
        SetState(UpdateState.Failed);
    }

    private void SetState(UpdateState state)
    {
        State = state;
        if (state != UpdateState.Failed) Error = null;
        Raise();
    }

    private void Raise()
    {
        if (_context != null)
            _context.Post(_ => StateChanged?.Invoke(this, EventArgs.Empty), null);
        else
            StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Log(string message) => _options.Log?.Invoke($"[AutoUpdate] {message}");

    public void Dispose()
    {
        _lifetime.Cancel();
        _http.Dispose();
        if (Current == this) Current = null;
    }
}
