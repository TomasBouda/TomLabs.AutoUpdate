using System.IO.Compression;
using System.Security.Cryptography;

namespace TomLabs.AutoUpdate;

/// <summary>Downloads an update asset, verifies its SHA-256 and extracts the executable from the zip.</summary>
internal static class UpdateDownloader
{
    /// <summary>Returns the path of the verified new executable.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, string downloadDirectory, string executableName,
        bool requireChecksum, HttpClient http, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (requireChecksum && string.IsNullOrEmpty(update.Sha256))
            throw new InvalidOperationException("The manifest has no SHA-256 for this asset; refusing to install an unverified build.");

        var targetDir = Path.Combine(downloadDirectory, update.Version.ToString());
        Directory.CreateDirectory(targetDir);
        var archivePath = Path.Combine(targetDir, update.FileName);
        var exePath = Path.Combine(targetDir, update.ExecutableName ?? executableName);

        // A previous run may already have this build verified and extracted.
        if (File.Exists(exePath) && File.Exists(archivePath) && await HashMatchesAsync(archivePath, update.Sha256, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(1);
            return exePath;
        }

        using (var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Size;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

            var buffer = new byte[1 << 16];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                read += n;
                if (total is > 0) progress?.Report(Math.Min(0.99, (double)read / total.Value));
            }
        }

        if (!string.IsNullOrEmpty(update.Sha256) && !await HashMatchesAsync(archivePath, update.Sha256, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(archivePath);
            throw new ChecksumMismatchException(update.Version.ToString());
        }

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ExtractExecutable(archivePath, exePath);
        else
            File.Copy(archivePath, exePath, overwrite: true);

        progress?.Report(1);
        return exePath;
    }

    private static void ExtractExecutable(string archivePath, string exePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var wanted = Path.GetFileName(exePath);
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"The archive contains no '{wanted}'.");
        entry.ExtractToFile(exePath, overwrite: true);
    }

    private static async Task<bool> HashMatchesAsync(string path, string? expectedSha256, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(expectedSha256)) return true;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes downloads of other versions so the folder does not grow forever.</summary>
    public static void Prune(string downloadDirectory, string? keepVersion)
    {
        try
        {
            if (!Directory.Exists(downloadDirectory)) return;
            foreach (var dir in Directory.GetDirectories(downloadDirectory))
            {
                if (keepVersion != null && string.Equals(Path.GetFileName(dir), keepVersion, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, recursive: true); } catch { /* still in use; next time */ }
            }
        }
        catch
        {
            // Housekeeping only.
        }
    }
}

/// <summary>
/// The downloaded asset is not the one the manifest described. Asset urls are per channel, not per version
/// (<c>…/dl/App/stable/App-win-x64.zip</c>), so this is what a release landing between reading the manifest and
/// downloading the asset looks like; the updater re-reads the manifest and tries once more.
/// </summary>
public sealed class ChecksumMismatchException : Exception
{
    public ChecksumMismatchException(string version)
        : base($"Downloaded file failed the SHA-256 check (expected the asset of {version}).")
    {
        Version = version;
    }

    public string Version { get; }
}
