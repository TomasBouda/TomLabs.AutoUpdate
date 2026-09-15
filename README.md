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

## Requirements

- The app runs from a writable folder (portable exe). Program Files without elevation is refused with a
  clear reason; the banner then stays hidden.
- .NET 8 or newer, trimming-safe (System.Text.Json source generation).
