using System.Diagnostics.CodeAnalysis;

namespace TomLabs.AutoUpdate;

/// <summary>
/// Minimal semantic version: <c>major.minor.patch[-prerelease][+build]</c>.
/// Build metadata is kept for display but ignored when comparing; a pre-release sorts below its release
/// (<c>1.2.0-nightly.abc1234</c> &lt; <c>1.2.0</c>), exactly what an updater needs.
/// </summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }
    public string? BuildMetadata { get; }

    public bool IsPrerelease => Prerelease != null;

    /// <summary>The version without pre-release label and metadata (<c>1.2.0-nightly.abc</c> → <c>1.2.0</c>).</summary>
    public SemVersion Core => new(Major, Minor, Patch);

    public SemVersion(int major, int minor, int patch, string? prerelease = null, string? buildMetadata = null)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = string.IsNullOrEmpty(prerelease) ? null : prerelease;
        BuildMetadata = string.IsNullOrEmpty(buildMetadata) ? null : buildMetadata;
    }

    public static SemVersion Parse(string text)
        => TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a semantic version.");

    public static bool TryParse(string? text, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];

        string? build = null;
        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            build = s[(plus + 1)..];
            s = s[..plus];
        }

        string? prerelease = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..];
            s = s[..dash];
        }

        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4) return false;

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length) { numbers[i] = 0; continue; }
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0) return false;
        }

        version = new SemVersion(numbers[0], numbers[1], numbers[2], prerelease, build);
        return true;
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;

        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // A release is newer than any of its pre-releases.
        if (Prerelease is null && other.Prerelease is null) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static int ComparePrerelease(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            if (i >= pa.Length) return -1;
            if (i >= pb.Length) return 1;

            var na = int.TryParse(pa[i], out var ia);
            var nb = int.TryParse(pb[i], out var ib);
            var c = (na, nb) switch
            {
                (true, true) => ia.CompareTo(ib),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(pa[i], pb[i]),
            };
            if (c != 0) return c;
        }
        return 0;
    }

    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;

    /// <summary>Version without build metadata, e.g. <c>1.2.0-nightly.abc1234</c>.</summary>
    public override string ToString()
        => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
