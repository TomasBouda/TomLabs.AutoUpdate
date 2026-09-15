using System.Text.Json.Serialization;

namespace TomLabs.AutoUpdate;

public enum UpdateChannel
{
    Stable,
    Nightly,
}

/// <summary>
/// The one description of a published build that every source produces: attached as <c>update.json</c> to a
/// GitHub release by the shared publish workflow, or served as <c>latest.json</c> from a plain web server.
/// </summary>
public sealed class UpdateManifest
{
    /// <summary>Application name; must match <see cref="UpdateOptions.AppName"/> so a manifest cannot be mixed up between apps.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Semantic version of the build, e.g. <c>1.2.0</c> or <c>1.2.0-nightly.abc1234</c>.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Full commit hash the build was made from.</summary>
    public string? Commit { get; set; }

    public string? Channel { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Release notes (Markdown or plain text).</summary>
    public string? Notes { get; set; }

    /// <summary>Web page with the full notes (the GitHub release page).</summary>
    public string? NotesUrl { get; set; }

    public List<UpdateAsset> Assets { get; set; } = new();
}

public sealed class UpdateAsset
{
    /// <summary>Runtime identifier the asset was built for, e.g. <c>win-x64</c>.</summary>
    public string Rid { get; set; } = string.Empty;

    /// <summary>File name of the zip (or bare exe) in the release.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Absolute download URL, or null/relative to be resolved by the source (release asset by name, or relative to the manifest URL).</summary>
    public string? Url { get; set; }

    /// <summary>Lower-case hex SHA-256 of the downloaded file.</summary>
    public string? Sha256 { get; set; }

    public long? Size { get; set; }

    /// <summary>Name of the executable inside the zip; defaults to the running executable's file name.</summary>
    public string? Executable { get; set; }
}

/// <summary>A resolved update the app can offer: manifest data plus the asset that matches this machine.</summary>
public sealed record UpdateInfo(
    SemVersion Version,
    string? Commit,
    DateTimeOffset? PublishedAt,
    string? Notes,
    string? NotesUrl,
    string DownloadUrl,
    string FileName,
    string? Sha256,
    long? Size,
    string? ExecutableName)
{
    public string? ShortCommit => Commit is { Length: >= 7 } c ? c[..7] : Commit;
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(GitHubRelease))]
internal partial class UpdateJsonContext : JsonSerializerContext
{
}

/// <summary>The few fields of the GitHub "get a release" response the source needs.</summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}
