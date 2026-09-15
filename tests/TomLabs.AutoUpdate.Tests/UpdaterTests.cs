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
        public string? Signature { get; set; }
        public Task<UpdateFetchResult?> FetchAsync(UpdateChannel channel, HttpClient http, CancellationToken cancellationToken)
            => Task.FromResult(Manifest is null ? null : new UpdateFetchResult(Manifest, System.Text.Encoding.UTF8.GetBytes("{}"), Signature));
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

public class SignatureTests
{
    private sealed class SignedSource : IUpdateSource
    {
        private readonly byte[] _raw;
        private readonly string? _sig;
        public SignedSource(byte[] raw, string? sig) { _raw = raw; _sig = sig; }
        public Task<UpdateFetchResult?> FetchAsync(UpdateChannel channel, HttpClient http, CancellationToken cancellationToken)
            => Task.FromResult<UpdateFetchResult?>(new UpdateFetchResult(
                System.Text.Json.JsonSerializer.Deserialize<UpdateManifest>(_raw, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!, _raw, _sig));
        public Uri ResolveAssetUrl(UpdateManifest manifest, UpdateAsset asset) => new("https://example.invalid/" + asset.File);
    }

    private static (string publicPem, string signature, byte[] raw) Sign(string json)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var raw = System.Text.Encoding.UTF8.GetBytes(json);
        var sig = key.SignData(raw, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);
        return (key.ExportSubjectPublicKeyInfoPem(), Convert.ToBase64String(sig), raw);
    }

    private static string ManifestJson(string version) =>
        $$"""{ "name": "TestApp", "version": "{{version}}", "assets": [ { "rid": "{{RidForTests()}}", "file": "TestApp.zip", "sha256": "00" } ] }""";

    private static string RidForTests() => typeof(Updater).Assembly.GetType("TomLabs.AutoUpdate.RuntimeRid")!.GetProperty("Current")!.GetValue(null)!.ToString()!;

    private static async Task<Updater> StartAsync(IUpdateSource source, string publicPem)
    {
        var exe = Path.Combine(Path.GetTempPath(), $"TestApp-{Guid.NewGuid():N}.exe");
        File.WriteAllText(exe, "x");
        var options = new UpdateOptions("TestApp", source)
        {
            Build = new AppBuildInfo(SemVersion.Parse("1.0.0"), null, exe),
            CheckOnStartup = false, CheckInterval = TimeSpan.Zero, DownloadAutomatically = false,
            PublicKeyPem = publicPem,
        };
        var u = Updater.Start(options);
        await u.CheckAsync();
        return u;
    }

    [Fact]
    public async Task ValidSignatureIsAccepted()
    {
        var (pem, sig, raw) = Sign(ManifestJson("2.0.0"));
        using var u = await StartAsync(new SignedSource(raw, sig), pem);
        Assert.Equal(UpdateState.Available, u.State);
    }

    [Fact]
    public async Task TamperedManifestIsRejected()
    {
        var (pem, sig, _) = Sign(ManifestJson("2.0.0"));
        var tampered = System.Text.Encoding.UTF8.GetBytes(ManifestJson("3.0.0"));
        using var u = await StartAsync(new SignedSource(tampered, sig), pem);
        Assert.Equal(UpdateState.Failed, u.State);
        Assert.Contains("signature", u.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingSignatureIsRejectedWhenKeyConfigured()
    {
        var (pem, _, raw) = Sign(ManifestJson("2.0.0"));
        using var u = await StartAsync(new SignedSource(raw, null), pem);
        Assert.Equal(UpdateState.Failed, u.State);
    }
}
