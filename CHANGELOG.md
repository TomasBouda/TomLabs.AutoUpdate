# Changelog

## 0.2.3 — 2026-09-18

- Added: the publish workflow can upload the same release to the TomLabs app store (`store-url` input, `store-token` secret).

## 0.2.2 — 2026-09-18

- Changed: the manifest signature is verified only for a build that is newer than the running one, so an older unsigned release no longer reports an error.

## 0.2.2 — 2026-09-17

- Added: `UpdateOptions.AccessToken`, sent as `Authorization: Bearer` with every manifest and download request — what a private app on the TomLabs app store needs (one of the store's download tokens), and enough for GitHub Releases of a private repository.

## 0.2.1 — 2026-09-17

- Fixed: on the nightly channel a build whose commit hash sorted lexically lower than the running one was never offered; nightly now compares the core version and treats any other commit as newer.

## 0.2.0 — 2026-09-15

- Added: optional ECDSA P-256 manifest signatures (`update.json.sig`, `UpdateOptions.PublicKeyPem`, `signing-key` secret of the publish workflow).
- Added: rollback guard — when an updated build does not reach `MarkHealthy()`, the next launch restores the previous executable.
- Changed: `IUpdateSource.FetchAsync` returns `UpdateFetchResult` (manifest + raw bytes + signature).

## 0.1.1 — 2026-09-15

- Fixed: the Avalonia banner showed no text or button in trimmed apps — bindings are now compiled.

## 0.1.0 — 2026-09-15

- Initial release: GitHub Releases and manifest sources, SHA-256 verification, in-place executable swap with
  rollback backup, stable/nightly channels, Avalonia update banner, reusable publish workflow.
