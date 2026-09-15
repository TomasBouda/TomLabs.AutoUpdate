namespace TomLabs.AutoUpdate;

/// <summary>Configuration handed to <see cref="Updater.Start"/>. Only <see cref="AppName"/> and <see cref="Source"/> are required.</summary>
public sealed class UpdateOptions
{
    public UpdateOptions(string appName, IUpdateSource source)
    {
        AppName = appName;
        Source = source;
    }

    /// <summary>Must match the manifest's <c>name</c>; also names the folder under %LOCALAPPDATA%.</summary>
    public string AppName { get; }

    public IUpdateSource Source { get; }

    /// <summary>Channel to follow; null means "whatever the build is" (nightly builds follow nightly, releases follow stable).</summary>
    public UpdateChannel? Channel { get; set; }

    /// <summary>Called when the user switches channel so the app can persist it.</summary>
    public Action<UpdateChannel>? ChannelChanged { get; set; }

    /// <summary>Check shortly after startup (default true).</summary>
    public bool CheckOnStartup { get; set; } = true;

    /// <summary>Delay before the startup check, so it never competes with the app's own loading.</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Periodic re-check while the app runs; <see cref="TimeSpan.Zero"/> disables it.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Download as soon as an update is found so "Update" only needs a restart (default true).</summary>
    public bool DownloadAutomatically { get; set; } = true;

    /// <summary>Refuse assets without a SHA-256 in the manifest (default true).</summary>
    public bool RequireChecksum { get; set; } = true;

    /// <summary>Override the build info (tests, or an app whose entry assembly is not the versioned one).</summary>
    public AppBuildInfo? Build { get; set; }

    /// <summary>Where downloads are kept; defaults to %LOCALAPPDATA%\{AppName}\updates.</summary>
    public string? DownloadDirectory { get; set; }

    /// <summary>Exits the current process after the new executable has been launched; defaults to <see cref="Environment.Exit"/>.</summary>
    public Action? ExitApplication { get; set; }

    /// <summary>Extra command line passed to the relaunched executable, after the built-in <c>--updated</c> flag.</summary>
    public string? RelaunchArguments { get; set; }

    /// <summary>Diagnostics sink; hook it to the app's logger.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Sent as the User-Agent (GitHub requires one); defaults to "{AppName}/{version} TomLabs.AutoUpdate".</summary>
    public string? UserAgent { get; set; }
}

public enum UpdateState
{
    /// <summary>Not started, or the app is not running from a real executable (e.g. <c>dotnet run</c>).</summary>
    Disabled,
    Idle,
    Checking,
    UpToDate,
    /// <summary>A newer build exists and has not been downloaded (auto-download off, or it failed).</summary>
    Available,
    Downloading,
    /// <summary>The new executable is verified and waiting for a restart.</summary>
    ReadyToInstall,
    Failed,
}
