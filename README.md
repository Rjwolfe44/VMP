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
- The dedicated server targets .NET 10. The KSP client stays on .NET Framework 4.7.2.
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

Public releases should contain runtime artifacts only:

- `VladMultiplayer-client.zip` for players.
- `VladMultiplayer-server.zip` for dedicated-server hosts.

Test projects, local `VMPServer-test` data, private `.env` files, KSP managed DLL source folders, and master-server binaries are not release assets.

Local release publishing uses the GitHub CLI, matching the simple asset-upload flow used by vladmod:

```powershell
./release.ps1 -Version 0.2.0
```

Run `gh auth login` once before publishing. If the tag/release already exists and you only need to rebuild or replace assets, use:

```powershell
./release.ps1 -Version 0.2.0 -SkipCommit
```

The tag workflow is still available for a Windows self-hosted GitHub Actions runner because the client build needs local KSP managed DLLs.

- Push a tag like `v0.1.0` to trigger a release build.
- The workflow bootstraps local dependencies, packages the client and dedicated server zips, uploads the workflow artifacts, and publishes a GitHub release on tag builds.
- Set the optional repository variable `VMP_KSP_ROOT` if the runner cannot auto-detect your KSP install.

## Installing The Client

Unzip `VladMultiplayer-client.zip` into the KSP root folder. After extraction, `GameData` should contain both `000_Harmony` and `VladMultiplayer`.

Do not put the dedicated server files in `GameData`.

## Hosting A Dedicated Server

Unzip `VladMultiplayer-server.zip` outside the KSP folder, for example on the desktop or in a server directory.

- Windows: run `Server.exe`.
- Portable builds need the .NET 10 runtime: run `dotnet Server.dll` from the extracted `VMPServer` folder. Windows self-contained builds include the runtime. Screenshot sharing uses Windows System.Drawing; other hosting platforms need a separate image backend before that feature is supported.

The default game port is UDP `8800` (`Config/ConnectionSettings.xml`). If you enable the server web/status page, its default port is TCP `8900` (`Config/WebsiteSettings.xml`). For public hosting behind Starlink IPv4 CGNAT, use reachable IPv6 or a tunnel such as ZeroTier/playit; classic IPv4 port forwarding may not be possible.

The client and server default to 1200-byte packets with MTU auto-expansion disabled to leave room for tunnel overhead. UPnP is off by default. Master-server registration is discovery and does not guarantee successful NAT traversal. A Playit tunnel must carry UDP; leave Proxy Protocol set to None.

For private hosting, use a server password, disable master-server listing, and keep the web/status listener disabled or bound to localhost. ZeroTier's Windows network profile is often Public, so allow the game on that profile with a rule limited to your overlay subnet and server program.

`Scripts/Manage-VmpServer.ps1` provides Start, Stop, Restart, Status, and Backup for the Windows scheduled setup. Stop and Backup use `shutdown.request` to run the server's normal save path before exiting. Keep the server folder writable only by trusted local users. Daily backups briefly stop the server, archive Universe and Config, and resume it.

`Scripts/New-FriendPack.ps1` creates a private pack from an installed GameData tree with SHA256 file manifests and a separate settings template. Stock KSP, DLC, player identity data, and generated ModuleManager caches are excluded. Its included installer checks KSP/DLC, verifies hashes, backs up existing mods, sets a distinct player name, and verifies the installed files. Each player supplies their own KSP and DLC installation.

## Master Servers

VMP discovery is backed by the repo itself. Clients, dedicated servers, and master servers read the fork URLs in `LmpGlobal/RepoConstants.cs`, and they fall back to shipped `MasterServersList/*.txt` copies if GitHub raw is temporarily unavailable.

Master-server binaries are not included in normal releases. To run a public VMP browser, build or containerize the master server from source:

```powershell
dotnet msbuild .\MasterServer\MasterServer.csproj /p:Configuration=Release /p:Platform=AnyCPU
.\MasterServer\bin\Release\MasterServer.exe /noupdatecheck
```

The master server listens on UDP `8700` for server registration, server-list requests, and NAT introduction. It listens on TCP `8701` for the web/json server list. Open both ports on the host or publish them from the Docker container.

After the master server is reachable, add its `host:8700` entry to `MasterServersList/MasterServersList.txt` in this repo. Dedicated servers appear in the browser when `RegisterWithMasterServer=true` in `Config/MasterServerSettings.xml` and the server can reach a listed VMP master server.

Do not use public LunaMP master servers as VMP defaults. VMP advertises `ProtocolForkId` and `ExactSessionBuild` so clients can hide incompatible servers and servers can reject incompatible clients. Stock LunaMP master servers do not preserve those VMP fields, so they cannot reliably advertise VMP-only features or guarantee compatible NAT introduction. Use a VMP-compatible master server instead.

## Notes

Some internal names still say `Lmp`, `Luna`, or `Kspmp`. That is intentional for the first implementation pass: the visible product identity and protocol/install separation are being changed first, while internal code renames wait until builds and friend testing are stable.
