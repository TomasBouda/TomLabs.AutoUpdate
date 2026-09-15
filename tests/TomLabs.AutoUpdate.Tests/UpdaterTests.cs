using System.Net;
using System.Text;
using TomLabs.AutoUpdate;
using Xunit;

namespace TomLabs.AutoUpdate.Tests;

public class UpdaterTests
{
    private sealed class FakeSource : IUpdateSource
    {
        public UpdateManifest? Manifest { get; set; }
        public Task<UpdateManifest?> FetchAsync(UpdateChannel channel, HttpClient http, CancellationToken cancellationToken) => Task.FromResult(Manifest);
        public Uri ResolveAssetUrl(UpdateManifest manifest, UpdateAsset asset) => new("https://example.invalid/" + asset.File);
    }

    private static UpdateOptions Options(FakeSource source, string version, string? commit = null, UpdateChannel? channel = null) =>
        new("TestApp", source)
        {
            Build = new AppBuildInfo(SemVersion.Parse(version), commit, Path.Combine(Path.GetTempPath(), "not-an-exe")),
            Channel = channel,
            CheckOnStartup = false,
            CheckInterval = TimeSpan.Zero,
            DownloadAutomatically = false,
        };

    private static UpdateManifest Manifest(string version, string? commit = null) => new()
    {
        Name = "TestApp",
        Version = version,
        Commit = commit,
        Assets = { new UpdateAsset { Rid = RuntimeRidForTests(), File = "TestApp.zip", Sha256 = "00" } },
    };

    private static string RuntimeRidForTests() => typeof(Updater).Assembly.GetType("TomLabs.AutoUpdate.RuntimeRid")!
        .GetProperty("Current")!.GetValue(null)!.ToString()!;

    [Fact]
    public async Task StableChannelOffersHigherVersionOnly()
    {
        var source = new FakeSource { Manifest = Manifest("0.5.0") };
        using var updater = Updater.Start(Options(source, "0.5.0"));
        Assert.Equal(UpdateState.Disabled, updater.State); // not running from an exe in tests

        // Evaluate directly through a checkable instance: use a writable fake exe path.
        var exe = Path.Combine(Path.GetTempPath(), $"TestApp-{Guid.NewGuid():N}.exe");
        File.WriteAllText(exe, "x");
        try
        {
            var options = Options(source, "0.5.0");
            options.Build = new AppBuildInfo(SemVersion.Parse("0.5.0"), null, exe);
            using var u = Updater.Start(options);
            Assert.False(await u.CheckAsync());
            Assert.Equal(UpdateState.UpToDate, u.State);

            source.Manifest = Manifest("0.6.0");
            Assert.True(await u.CheckAsync());
            Assert.Equal(UpdateState.Available, u.State);
            Assert.Equal("0.6.0", u.Available!.Version.ToString());
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public async Task NightlyChannelTreatsDifferentCommitAsUpdate()
    {
        var exe = Path.Combine(Path.GetTempPath(), $"TestApp-{Guid.NewGuid():N}.exe");
        File.WriteAllText(exe, "x");
        try
        {
            var source = new FakeSource { Manifest = Manifest("0.5.0-nightly.aaaaaaa", "aaaaaaa1111") };
            var options = Options(source, "0.5.0-nightly.aaaaaaa", "aaaaaaa1111", UpdateChannel.Nightly);
            options.Build = new AppBuildInfo(SemVersion.Parse("0.5.0-nightly.aaaaaaa"), "aaaaaaa1111", exe);
            using var u = Updater.Start(options);
            Assert.False(await u.CheckAsync());

            source.Manifest = Manifest("0.5.0-nightly.bbbbbbb", "bbbbbbb2222");
            Assert.True(await u.CheckAsync());
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public async Task ManifestForAnotherAppIsIgnored()
    {
        var exe = Path.Combine(Path.GetTempPath(), $"TestApp-{Guid.NewGuid():N}.exe");
        File.WriteAllText(exe, "x");
        try
        {
            var manifest = Manifest("9.0.0");
            manifest.Name = "OtherApp";
            var source = new FakeSource { Manifest = manifest };
            var options = Options(source, "0.5.0");
            options.Build = new AppBuildInfo(SemVersion.Parse("0.5.0"), null, exe);
            using var u = Updater.Start(options);
            Assert.False(await u.CheckAsync());
        }
        finally
        {
            File.Delete(exe);
        }
    }
}
