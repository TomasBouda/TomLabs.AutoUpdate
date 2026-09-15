using System.Reflection;

namespace TomLabs.AutoUpdate;

/// <summary>
/// What the running build is: version, channel and commit, all read from
/// <see cref="AssemblyInformationalVersionAttribute"/> (<c>1.2.0+sha</c> for a release,
/// <c>1.2.0-nightly.abc1234+sha</c> for a continuous build), so nothing is hand-maintained.
/// </summary>
public sealed record AppBuildInfo(SemVersion Version, string? Commit, string ExecutablePath)
{
    /// <summary>A build whose pre-release label starts with the nightly tag follows the nightly channel by default.</summary>
    public bool IsNightly => Version.Prerelease?.StartsWith("nightly", StringComparison.OrdinalIgnoreCase) == true;

    public UpdateChannel DefaultChannel => IsNightly ? UpdateChannel.Nightly : UpdateChannel.Stable;

    /// <summary>Short commit for display (7 characters), or null when the build carries no metadata.</summary>
    public string? ShortCommit => Commit is { Length: >= 7 } c ? c[..7] : Commit;

    public static AppBuildInfo FromEntryAssembly() => FromAssembly(Assembly.GetEntryAssembly());

    public static AppBuildInfo FromAssembly(Assembly? assembly)
    {
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        SemVersion? version = null;
        string? commit = null;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            SemVersion.TryParse(informational, out version);
            var plus = informational.IndexOf('+');
            if (plus >= 0 && plus < informational.Length - 1)
                commit = informational[(plus + 1)..];
        }

        if (version is null)
        {
            var v = assembly?.GetName().Version;
            version = v is null ? new SemVersion(0, 0, 0) : new SemVersion(v.Major, v.Minor, Math.Max(v.Build, 0));
        }

        return new AppBuildInfo(version, commit, Environment.ProcessPath ?? string.Empty);
    }
}
