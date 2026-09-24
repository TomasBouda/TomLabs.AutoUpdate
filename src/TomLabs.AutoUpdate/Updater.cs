using System.Net.Http.Headers;
using System.Security.Cryptography;

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

    /// <summary>True when this launch restored the previous executable because the updated build failed to start.</summary>
    public bool RolledBack { get; private set; }

    private bool _healthy;

    /// <summary>How soon the updater looks again while the only thing in its way is another instance that is exiting.</summary>
    private static readonly TimeSpan OtherInstanceRetryInterval = TimeSpan.FromSeconds(30);

    private bool _disabledUntilTheOtherInstanceExits;

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
        if (!string.IsNullOrEmpty(options.AccessToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
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

        // A build that has just replaced its predecessor starts while the old process is still exiting, so the
        // "another instance is running" guard would fire on every single update. Leave the verdict to the first
        // check, by which time the handover is over.
        if (updater.WasJustUpdated && UpdateApplier.IsOtherInstanceRunning(updater.Build.ExecutablePath))
            updater.Log("Started by an update while the previous instance is still exiting; deciding at the first check.");
        else
            updater.RefreshAvailability();

        if (updater.TryRollbackFailedStart())
            return updater;

        if (updater.State != UpdateState.Disabled)
            updater.SetState(UpdateState.Idle);
        _ = updater.RunBackgroundAsync();
        return updater;
    }

    /// <summary>True when this process was launched by <see cref="UpdateApplier.Apply"/> to replace the previous build.</summary>
    public bool WasJustUpdated { get; } =
        Array.Exists(Environment.GetCommandLineArgs(), a => a.Equals(UpdateApplier.UpdatedArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Asks whether an update could be applied right now and moves in and out of <see cref="UpdateState.Disabled"/>
    /// accordingly. Every reason can go away while the app runs — another instance closes, a folder becomes
    /// writable — so the verdict is never latched: it is taken again before every check.
    /// </summary>
    private bool RefreshAvailability()
    {
        var reason = UpdateApplier.CheckCanApply(Build.ExecutablePath);
        if (reason != null)
        {
            if (reason != DisabledReason)
                Log($"In-place update unavailable: {reason}");
            DisabledReason = reason;
            _disabledUntilTheOtherInstanceExits = reason == UpdateApplier.OtherInstanceReason;
            if (State != UpdateState.Disabled)
                SetState(UpdateState.Disabled);
            return false;
        }

        if (DisabledReason != null || State == UpdateState.Disabled)
        {
            Log("In-place update is possible again.");
            DisabledReason = null;
            _disabledUntilTheOtherInstanceExits = false;
            SetState(UpdateState.Idle);
        }
        return true;
    }

    /// <summary>
    /// Call once the app is usable (main window shown): removes the previous executable kept as rollback.
    /// Without the call the start is treated as healthy after <see cref="UpdateOptions.HealthyAfter"/>.
    /// </summary>
    public void MarkHealthy()
    {
        if (_healthy) return;
        _healthy = true;
        TryDelete(StartMarkerPath);
        _ = UpdateApplier.CleanUpAsync(Build.ExecutablePath, _lifetime.Token);
    }

    private string StartMarkerPath => Path.Combine(DownloadDirectory, "starting.marker");

    /// <summary>
    /// A marker is written while a freshly updated build starts and removed by <see cref="MarkHealthy"/>.
    /// Finding it at the next launch means the previous start never got that far: restore the backup and relaunch.
    /// </summary>
    private bool TryRollbackFailedStart()
    {
        try
        {
            var hasBackup = UpdateApplier.HasBackup(Build.ExecutablePath);
            if (!hasBackup)
            {
                TryDelete(StartMarkerPath);
                return false;
            }

            if (File.Exists(StartMarkerPath) && _options.RollbackOnFailedStart)
            {
                Log("The previous start of this build did not complete; restoring the previous version.");
                TryDelete(StartMarkerPath);
                UpdateApplier.Rollback(Build.ExecutablePath);
                RolledBack = true;
                var start = new System.Diagnostics.ProcessStartInfo(Build.ExecutablePath) { UseShellExecute = true };
                System.Diagnostics.Process.Start(start);
                (_options.ExitApplication ?? (() => Environment.Exit(0)))();
                return true;
            }

            Directory.CreateDirectory(DownloadDirectory);
            File.WriteAllText(StartMarkerPath, Build.Version.ToString());
            _ = Task.Delay(_options.HealthyAfter, _lifetime.Token).ContinueWith(_ => MarkHealthy(), TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        catch (Exception ex)
        {
            Log($"Rollback guard failed: {ex.Message}");
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private async Task RunBackgroundAsync()
    {
        var token = _lifetime.Token;
        try
        {
            UpdateDownloader.Prune(DownloadDirectory, keepVersion: null);

            if (_options.CheckOnStartup)
            {
                await Task.Delay(_options.StartupDelay, token).ConfigureAwait(false);
                await CheckAsync(token).ConfigureAwait(false);
            }

            while (_options.CheckInterval > TimeSpan.Zero)
            {
                // An instance that is only waiting for its predecessor to exit looks again in seconds, not hours.
                var wait = _disabledUntilTheOtherInstanceExits && OtherInstanceRetryInterval < _options.CheckInterval
                    ? OtherInstanceRetryInterval
                    : _options.CheckInterval;
                await Task.Delay(wait, token).ConfigureAwait(false);
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
        // Taken again every time: what blocked an update a minute ago (another instance still exiting after an
        // update) is usually gone by now, and the app must not stay "updates off" for the rest of its life.
        if (!RefreshAvailability()) return false;
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Available != null;

        try
        {
            SetState(UpdateState.Checking);
            var fetched = await _options.Source.FetchAsync(_channel, _http, cancellationToken).ConfigureAwait(false);
            LastCheck = DateTimeOffset.Now;

            var update = fetched is null ? null : Evaluate(fetched.Manifest);
            if (update is null)
            {
                Available = null;
                SetState(UpdateState.UpToDate);
                return false;
            }

            // Only a build we would actually install needs a valid signature; an older unsigned release is simply not an update.
            if (_options.PublicKeyPem != null)
                VerifySignature(fetched!);

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

        // Nightly labels carry a commit hash, which has no order: compare the core version only and treat a
        // different commit of the same version as the newer build (the rolling pre-release is always the latest).
        var newer = _channel == UpdateChannel.Nightly
            ? version.Core > Build.Version.Core
              || (version.Core.Equals(Build.Version.Core) && !string.IsNullOrEmpty(manifest.Commit) && !SameCommit(manifest.Commit, Build.Commit))
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

    /// <summary>ECDSA P-256 / SHA-256 over the exact manifest bytes; any mismatch rejects the manifest.</summary>
    private void VerifySignature(UpdateFetchResult fetched)
    {
        if (string.IsNullOrWhiteSpace(fetched.SignatureBase64))
            throw new InvalidOperationException("The manifest is not signed and this app requires signed updates.");

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(_options.PublicKeyPem);
        var signature = Convert.FromBase64String(fetched.SignatureBase64);
        if (!ecdsa.VerifyData(fetched.RawJson, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new InvalidOperationException("The manifest signature is invalid.");
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
            try
            {
                _downloadedExecutable = await UpdateDownloader.DownloadAsync(update, DownloadDirectory, exeName,
                    _options.RequireChecksum, _http, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (ChecksumMismatchException ex)
            {
                // The asset url belongs to the channel, not to one version (…/dl/App/stable/App-win-x64.zip), so a
                // release published between reading the manifest and downloading it serves different bytes. Read the
                // manifest again and take whatever is current now; a mismatch on the same version is a real failure.
                Log($"{ex.Message} Re-reading the manifest in case a newer build was published meanwhile.");
                var fetched = await _options.Source.FetchAsync(_channel, _http, cancellationToken).ConfigureAwait(false);
                var refreshed = fetched is null ? null : Evaluate(fetched.Manifest);
                if (refreshed is null || refreshed.Version == update.Version)
                    throw;

                if (_options.PublicKeyPem != null)
                    VerifySignature(fetched!);

                Log($"The manifest now offers {refreshed.Version}; downloading that instead.");
                update = refreshed;
                Available = refreshed;
                _downloadedExecutable = await UpdateDownloader.DownloadAsync(update, DownloadDirectory, exeName,
                    _options.RequireChecksum, _http, progress, cancellationToken).ConfigureAwait(false);
            }
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

    private readonly object _raiseLock = new();

    private void Raise()
    {
        if (_context != null)
        {
            _context.Post(_ => StateChanged?.Invoke(this, EventArgs.Empty), null);
            return;
        }

        // No UI thread (console, tests): keep handlers from running concurrently.
        lock (_raiseLock)
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
