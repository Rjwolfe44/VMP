# Vlad Multiplayer (VMP)

Vlad Multiplayer is a KSP 1 multiplayer fork built from the KSPMulti/Luna Multiplayer codebase. The first milestone keeps the familiar LMP-style client/server flow, then gives us a safe place to add lower-desync vessel sync, adaptive vessel update rates, better diagnostics, and more modern hosting paths.

## Current Goal

- Keep the KSP client on the KSP 1 compatible .NET Framework target.
- Keep the proven Lidgren UDP client/server architecture for the first playable build.
- Use a separate `GameData/VladMultiplayer` install folder so VMP does not overwrite LMP or KSPMulti.
- Use the `VMP` Lidgren application id so VMP clients only talk to VMP servers.
- Avoid broad namespace/project renames until the fork builds cleanly.
- Defer zstd or major transport rewrites until after the first friend-test build.

## Upstream Credit

This fork is derived from KSPMulti, which is derived from Luna Multiplayer. Original authorship and a substantial portion of the codebase belong to those upstream projects and contributors. VMP keeps that lineage visible while moving development into a separately maintained fork.

## First Playable Scope

- Direct IP or private network play first.
- IPv6 hosting where the route is reachable.
- ZeroTier or playit tunnels when IPv4 CGNAT blocks inbound hosting.
- Preserve LMP/KSPMulti-style UX so existing players know what to do.
- Keep existing targeted compression for bulky game-state blobs.

## Layout

- `LmpClient` contains the KSP client plugin.
- `Server` contains the standalone dedicated server.
- `LmpCommon` and `LmpGlobal` contain shared protocol, layout, and repository constants.
- `Scripts` contains inherited build, deploy, and packaging helpers.

## Publishing Rules

- Only the VMP fork belongs in this repository. Reference repos, local KSP installs, and private test-server folders stay outside the repo.
- `External/KSPLibraries` is intentionally not committed. It is populated from a local KSP install because the stock KSP managed DLLs are not repository content.
- `External/Nuget` is intentionally not committed. It is a regenerated compatibility layout for legacy project hint paths.
- `Scripts/SetDirectories.bat` is intentionally not committed. Use `Scripts/SetDirectories.example.bat` as the local template.

## Bootstrap A Clone

Run this before the first full build on a new machine:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Scripts\BootstrapLocalBuild.ps1
```

What it does:

- restores NuGet packages into the global cache,
- recreates the ignored `External/Nuget` compatibility folders used by the legacy projects,
- copies the required KSP managed DLLs into `External/KSPLibraries` from a local KSP install.

If KSP is not in a common location, pass it explicitly:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Scripts\BootstrapLocalBuild.ps1 -KspRoot "C:\Path\To\Kerbal Space Program"
```

## Releases

Automatic client+server releases are possible, but the client build requires local KSP managed DLLs. Because of that, the release workflow is configured for a Windows self-hosted GitHub Actions runner.

- Push a tag like `v0.1.0` to trigger a release build.
- The workflow bootstraps local dependencies, packages the client and server zips, uploads the workflow artifacts, and publishes a GitHub release on tag builds.
- Set the optional repository variable `VMP_KSP_ROOT` if the runner cannot auto-detect your KSP install.

## Notes

Some internal names still say `Lmp`, `Luna`, or `Kspmp`. That is intentional for the first implementation pass: the visible product identity and protocol/install separation are being changed first, while internal code renames wait until builds and friend testing are stable.
