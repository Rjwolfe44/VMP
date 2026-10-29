# External Nuget

This folder is intentionally excluded from git.

Some legacy projects still reference package content through `External/Nuget/<Package>.<Version>` hint paths. The tracked source uses normal NuGet restore plus a local compatibility sync step instead of committing package payloads.

Use the bootstrap script from the repo root to recreate this layout:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Scripts\BootstrapLocalBuild.ps1 -SkipKspRefs
```