# TomLabs.AutoUpdate

Self-update for portable .NET desktop apps — no installer, no helper process.

- **Sources:** GitHub Releases (stable = latest release, nightly = rolling `nightly` pre-release) or a
  `latest.json` manifest on any HTTPS server.
- **Verification:** every asset carries a SHA-256 in the manifest; unverified builds are refused.
- **Apply:** the running `App.exe` is renamed to `App.exe.old`, the new one copied in and started with
  `--updated`; the `.old` file is the rollback and is removed on the next healthy start.
- **Channels:** a build with a `-nightly.<sha>` version suffix follows the nightly channel, a release
  follows stable; the user can switch.
- **UI:** `TomLabs.AutoUpdate.Avalonia` ships an `UpdateBanner` ("v1.2.0 available · Update") and a view model.

## Use in an app

```csharp
// App.axaml.cs, OnFrameworkInitializationCompleted
Updater.Start(new UpdateOptions("IISBlitz", new GitHubReleasesSource("TomasBouda", "IISBlitz"))
{
    Channel = settings.UpdateChannel,                 // null = follow the build
    ChannelChanged = ch => { settings.UpdateChannel = ch; settings.Save(); },
    Log = message => logger.Information(message),
    ExitApplication = () => desktop.Shutdown(),
});
```

```xml
<!-- App.axaml -->
<StyleInclude Source="avares://TomLabs.AutoUpdate.Avalonia/Themes/Default.axaml"/>

<!-- anywhere in the layout, e.g. the status bar -->
<update:UpdateBanner/>
```

Override `UpdateBannerBackground`, `UpdateBannerBorder`, `UpdateBannerForeground`, `UpdateBannerAccent`,
`UpdateBannerAccentSoft` and `UpdateBannerError` in `Application.Resources` to match your palette.

## Publish with the reusable workflow

```yaml
jobs:
  nightly:
    uses: TomasBouda/TomLabs.AutoUpdate/.github/workflows/publish-app.yml@main
    with:
      project: src/App/App.csproj
      app-name: App          # must equal UpdateOptions.AppName
      channel: nightly       # or stable, from a tag workflow
```

The workflow publishes `<App>-<rid>.zip` per RID plus `update.json`:

```json
{
  "name": "App", "version": "0.5.0-nightly.78a63f7", "commit": "…", "channel": "nightly",
  "publishedAt": "2026-09-15T16:48:00Z", "notesUrl": "https://github.com/…/releases/tag/nightly",
  "assets": [ { "rid": "win-x64", "file": "App-win-x64.zip", "sha256": "…", "size": 20786796, "executable": "App.exe" } ]
}
```

The version must come from the build (`<VersionPrefix>` in the csproj; the workflow appends
`-nightly.<sha>` for the nightly channel) — the updater reads `AssemblyInformationalVersion`.

## Signed manifests (optional, recommended)

Generate a key pair once (`openssl ecparam -name prime256v1 -genkey -noout -out private.pem`,
`openssl ec -in private.pem -pubout -out public.pem`), store the private key as the `UPDATE_SIGNING_KEY`
secret of each app repository and pass it to the workflow:

```yaml
    uses: TomasBouda/TomLabs.AutoUpdate/.github/workflows/publish-app.yml@master
    with: { … }
    secrets:
      signing-key: ${{ secrets.UPDATE_SIGNING_KEY }}
```

Embed the public key in the app (`PublicKeyPem = "-----BEGIN PUBLIC KEY-----…"`) and every manifest must then
carry a valid `update.json.sig`; a missing or tampered signature rejects the update.

## Private feeds

`UpdateOptions.AccessToken` is sent as `Authorization: Bearer …` with every manifest and download
request. A private app on the TomLabs app store (`"private": true` in its `appstore.json`) answers 404
to anonymous requests, so its updater needs one of the store's download tokens here; on GitHub a
fine-grained token with `contents: read` reaches the releases of a private repository the same way.

## Rollback

The previous executable stays as `App.exe.old` until the new build reports a healthy start — call
`Updater.Current?.MarkHealthy()` once the main window is up (or it is assumed after 60 s). If the new
build dies before that, the next launch restores the previous version automatically.

## Requirements

- The app runs from a writable folder (portable exe). Program Files without elevation is refused with a
  clear reason; the banner then stays hidden.
- .NET 8 or newer, trimming-safe (System.Text.Json source generation).
