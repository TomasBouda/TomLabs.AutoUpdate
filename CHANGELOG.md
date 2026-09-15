# Changelog

## 0.2.0 — 2026-09-15

- Added: optional ECDSA P-256 manifest signatures (`update.json.sig`, `UpdateOptions.PublicKeyPem`, `signing-key` secret of the publish workflow).
- Added: rollback guard — when an updated build does not reach `MarkHealthy()`, the next launch restores the previous executable.
- Changed: `IUpdateSource.FetchAsync` returns `UpdateFetchResult` (manifest + raw bytes + signature).

## 0.1.1 — 2026-09-15

- Fixed: the Avalonia banner showed no text or button in trimmed apps — bindings are now compiled.

## 0.1.0 — 2026-09-15

- Initial release: GitHub Releases and manifest sources, SHA-256 verification, in-place executable swap with
  rollback backup, stable/nightly channels, Avalonia update banner, reusable publish workflow.
