using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TomLabs.AutoUpdate;

/// <summary>A fetched manifest together with the exact bytes it was parsed from (what a signature covers).</summary>
public sealed record UpdateFetchResult(UpdateManifest Manifest, byte[] RawJson, string? SignatureBase64);

/// <summary>Where manifests come from. Implementations must be safe to call repeatedly and must not show UI.</summary>
public interface IUpdateSource
{
    /// <summary>Fetches the latest manifest for a channel; null when the channel has nothing published yet.</summary>
    Task<UpdateFetchResult?> FetchAsync(UpdateChannel channel, HttpClient http, CancellationToken cancellationToken);

    /// <summary>Turns a manifest asset URL (absolute, relative or missing) into an absolute download URL.</summary>
    Uri ResolveAssetUrl(UpdateManifest manifest, UpdateAsset asset);
}

/// <summary>
/// GitHub Releases: the stable channel reads the latest non-prerelease release, the nightly channel the
/// release under the rolling nightly tag. Each release must carry an <c>update.json</c> asset (the shared
/// publish workflow attaches it); the other assets are resolved by file name.
/// </summary>
public sealed class GitHubReleasesSource : IUpdateSource
{
    private readonly Dictionary<string, string> _assetUrls = new(StringComparer.OrdinalIgnoreCase);

    public string Owner { get; }
    public string Repository { get; }
    public string NightlyTag { get; init; } = "nightly";
    public string ManifestAssetName { get; init; } = "update.json";

    /// <summary>Detached signature asset (base64 DER ECDSA over the manifest bytes); optional unless the app requires signing.</summary>
    public string SignatureAssetName => ManifestAssetName + ".sig";

    public GitHubReleasesSource(string owner, string repository)
    {
        Owner = owner;
        Repository = repository;
    }

    public async Task<UpdateFetchResult?> FetchAsync(UpdateChannel channel, HttpClient http, CancellationToken cancellationToken)
    {
        var url = channel == UpdateChannel.Nightly
            ? $"https://api.github.com/repos/{Owner}/{Repository}/releases/tags/{NightlyTag}"
            : $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync(stream, UpdateJsonContext.Default.GitHubRelease, cancellationToken).ConfigureAwait(false);
        if (release is null) return null;

        _assetUrls.Clear();
        foreach (var asset in release.Assets)
        {
            if (asset.Name != null && asset.BrowserDownloadUrl != null)
                _assetUrls[asset.Name] = asset.BrowserDownloadUrl;
        }

        // A release published without the manifest (older tooling, manual upload) is not something we can install from.
        if (!_assetUrls.TryGetValue(ManifestAssetName, out var manifestUrl))
            return null;

        var raw = await http.GetByteArrayAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize(raw, UpdateJsonContext.Default.UpdateManifest);
        if (manifest is null) return null;

        string? signature = null;
        if (_assetUrls.TryGetValue(SignatureAssetName, out var signatureUrl))
            signature = (await http.GetStringAsync(signatureUrl, cancellationToken).ConfigureAwait(false)).Trim();

        manifest.NotesUrl ??= release.HtmlUrl;
        manifest.Notes ??= release.Body;
        manifest.PublishedAt ??= release.PublishedAt;
        return new UpdateFetchResult(manifest, raw, signature);
    }

    public Uri ResolveAssetUrl(UpdateManifest manifest, UpdateAsset asset)
    {
        if (!string.IsNullOrEmpty(asset.Url) && Uri.TryCreate(asset.Url, UriKind.Absolute, out var absolute))
            return absolute;
        if (_assetUrls.TryGetValue(asset.File, out var url))
            return new Uri(url);
        throw new InvalidOperationException($"Release has no asset named '{asset.File}'.");
    }
}

/// <summary>
/// A manifest served from any HTTPS location (own server, CDN): <c>https://dl.example.com/app/stable/latest.json</c>.
/// Relative asset URLs resolve against the manifest URL, so the zip can sit next to it.
/// </summary>
public sealed class ManifestSource : IUpdateSource
{
    private readonly Func<UpdateChannel, Uri> _manifestUrl;
    private Uri? _lastUrl;

    public ManifestSource(Uri manifestUrl) : this(_ => manifestUrl) { }

    /// <summary>Different manifest per channel, e.g. <c>channel => new Uri($"https://dl.example.com/app/{channel}/latest.json")</c>.</summary>
    public ManifestSource(Func<UpdateChannel, Uri> manifestUrl) => _manifestUrl = manifestUrl;

    public async Task<UpdateFetchResult?> FetchAsync(UpdateChannel channel, HttpClient http, CancellationToken cancellationToken)
    {
        _lastUrl = _manifestUrl(channel);
        using var response = await http.GetAsync(_lastUrl, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize(raw, UpdateJsonContext.Default.UpdateManifest);
        if (manifest is null) return null;

        // The detached signature sits next to the manifest: latest.json -> latest.json.sig
        string? signature = null;
        using var sigResponse = await http.GetAsync(new Uri(_lastUrl + ".sig"), cancellationToken).ConfigureAwait(false);
        if (sigResponse.IsSuccessStatusCode)
            signature = (await sigResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();

        return new UpdateFetchResult(manifest, raw, signature);
    }

    public Uri ResolveAssetUrl(UpdateManifest manifest, UpdateAsset asset)
    {
        var relative = string.IsNullOrEmpty(asset.Url) ? asset.File : asset.Url;
        if (Uri.TryCreate(relative, UriKind.Absolute, out var absolute)) return absolute;
        return new Uri(_lastUrl ?? _manifestUrl(UpdateChannel.Stable), relative);
    }
}

internal static class RuntimeRid
{
    /// <summary>"win-x64" / "win-arm64": the RID the running app was published for, used to pick the asset.</summary>
    public static string Current
    {
        get
        {
            var rid = RuntimeInformation.RuntimeIdentifier;
            if (!string.IsNullOrEmpty(rid) && !rid.Contains("any", StringComparison.OrdinalIgnoreCase) && rid.Contains('-'))
                return rid;

            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            return $"{os}-{arch}";
        }
    }
}
