using System.Diagnostics;

namespace TomLabs.AutoUpdate;

/// <summary>
/// Swaps the running executable for the new one without an installer or helper process.
/// Windows allows a running executable to be renamed, so the update is: move <c>App.exe</c> to
/// <c>App.exe.old</c>, copy the new file in, start it and exit. The <c>.old</c> file is the rollback
/// until the new build reports a healthy start, after which <see cref="CleanUp"/> removes it.
/// </summary>
public static class UpdateApplier
{
    public const string BackupSuffix = ".old";
    public const string UpdatedArgument = "--updated";

    /// <summary>Explains why an update cannot be applied in place, or null when it can.</summary>
    public static string? CheckCanApply(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath) || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return "The app is not running from an executable (e.g. started with dotnet run).";

        var directory = Path.GetDirectoryName(executablePath);
        if (directory is null) return "Cannot determine the application folder.";

        try
        {
            var probe = Path.Combine(directory, $".update-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"The application folder is not writable: {directory}";
        }

        if (OtherInstanceRunning(executablePath))
            return "Another instance of the application is running; close it first.";

        return null;
    }

    /// <summary>Replaces the executable and launches the new one. Throws when the swap fails (the original is restored).</summary>
    public static Process Apply(string executablePath, string newExecutablePath, string? relaunchArguments)
    {
        var reason = CheckCanApply(executablePath);
        if (reason != null) throw new InvalidOperationException(reason);

        var backup = executablePath + BackupSuffix;
        if (File.Exists(backup)) File.Delete(backup);

        File.Move(executablePath, backup);
        try
        {
            File.Copy(newExecutablePath, executablePath, overwrite: true);
        }
        catch
        {
            // Put the original back so the app keeps working.
            if (File.Exists(executablePath)) File.Delete(executablePath);
            File.Move(backup, executablePath);
            throw;
        }

        var arguments = string.IsNullOrWhiteSpace(relaunchArguments) ? UpdatedArgument : $"{UpdatedArgument} {relaunchArguments}";
        var start = new ProcessStartInfo(executablePath, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
        };
        return Process.Start(start) ?? throw new InvalidOperationException("The updated executable could not be started.");
    }

    /// <summary>Deletes the <c>.old</c> backup left by a previous update. Safe to call at every start; retries while the old process exits.</summary>
    public static async Task CleanUpAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        var backup = executablePath + BackupSuffix;
        for (var attempt = 0; attempt < 10 && File.Exists(backup); attempt++)
        {
            try
            {
                File.Delete(backup);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Synchronous variant of <see cref="CleanUpAsync"/> for callers without an async context.</summary>
    public static void CleanUp(string executablePath) => CleanUpAsync(executablePath).GetAwaiter().GetResult();

    /// <summary>True when the previous update left a backup that can be restored.</summary>
    public static bool HasBackup(string executablePath) => File.Exists(executablePath + BackupSuffix);

    /// <summary>Puts the previous executable back (used when a new build fails to start).</summary>
    public static void Rollback(string executablePath)
    {
        var backup = executablePath + BackupSuffix;
        if (!File.Exists(backup)) throw new FileNotFoundException("No previous version to restore.", backup);
        var broken = executablePath + ".broken";
        if (File.Exists(broken)) File.Delete(broken);
        File.Move(executablePath, broken);
        File.Move(backup, executablePath);
        File.Delete(broken);
    }

    private static bool OtherInstanceRunning(string executablePath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(executablePath);
            var me = Environment.ProcessId;
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id == me) continue;
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch
                    {
                        // Access denied to another user's process: assume it is a different copy.
                    }
                }
            }
        }
        catch
        {
            // Enumeration failed; do not block the update on that.
        }
        return false;
    }
}
