# Vlad Multiplayer (VMP)

Vlad Multiplayer is a multiplayer mod for Kerbal Space Program 1 (KSP 1.12). It is a fork of KSPMulti, which is derived from Luna Multiplayer, and it keeps their client/server flow: a KSP plugin connects over UDP (Lidgren) to a dedicated server that holds the shared universe.

## Upstream Credit

This fork is derived from KSPMulti, which is derived from Luna Multiplayer. Original authorship and a substantial portion of the codebase belong to those upstream projects and contributors. VMP keeps that lineage visible while moving development into a separately maintained fork.

## Features

- **Separate identity.** The client installs into `GameData/VladMultiplayer` and uses the `VMP` Lidgren application id, so it does not overwrite or talk to LMP or KSPMulti. The handshake and master-server messages carry `ProtocolForkId` and `ExactSessionBuild`, so incompatible clients and servers can reject each other.
- **One settings file.** The server keeps every setting in `Config/vmp-server.json`. When it starts with only the older per-section XML files, it imports them once, verifies the import by reading the new file back, and moves the XML files to `Config/legacy-xml-<time>/` (they are never deleted). The import and the rollback steps are described in [docs/admin-api.md](docs/admin-api.md#the-settings-file).
- **Local admin API.** A JSON API on `127.0.0.1` with a bearer token reports status, players, network statistics, the server log and backups, lists and changes every setting, creates, deletes and prunes backups, sends chat messages, kicks players and asks a player's game for its logs. Failed authentications are rate limited. See [docs/admin-api.md](docs/admin-api.md).
- **Verified backups.** While players are online the server writes compressed archives (`Backups/Universe-<time>.tar.zst`) of the universe and the settings, verifies each one by reading it back, keeps a SHA-256 sidecar and leaves out credentials. New screenshots are archived once a day. Retention keeps the newest archives plus one per day (`BackupKeepNewest`, `BackupKeepDailyDays`, `BackupKeepScreenshotArchives`, `BackupLegacyZipKeep`) and caps the size of the `Backups` folder (`BackupMaxTotalMegabytes`, 2048 by default). It runs after every backup and at server start, and on demand through the admin API, VmpControl or `Server.exe prune [--dir <folder>] [--dry-run]`. Single backups can be deleted the same way. `Server.exe backup --out <folder>` and `Server.exe restore <file> --to <folder>` work offline. All backup settings are in the `interval` section.
- **Message compression.** Vessel, kerbal, scenario, contract and craft payloads are compressed with zstd. The level adapts to the payload size and to the measured speed, and clients that cannot compress with zstd keep sending QuickLZ. See [docs/compression.md](docs/compression.md).
- **Diagnostics.** Clients record health data, and can send a log bundle to the server by hand, automatically after freezes, errors or a crash, or when the host asks for it (`requestlogs <player> [note]` in the server console). See [docs/diagnostics.md](docs/diagnostics.md). Tools that analyse the bundles are not part of this repository.
- **VmpControl.** A Windows tray application that monitors and operates a server through the admin API: status, players, network and compression figures, logs, backups, client reports, settings, and actions such as backup, say, kick, request logs, start, stop and restart. See [VmpControl/README.md](VmpControl/README.md).

## Layout

- `LmpClient` is the KSP client plugin (.NET Framework 4.7.2).
- `Server` is the standalone dedicated server (.NET 10).
- `LmpCommon` and `LmpGlobal` hold the shared protocol, compression, statistics, layout and repository constants.
- `LmpMasterServer`, `MasterServer` and `MasterServersList` contain the optional master server and the server lists.
- `VmpControl` is the tray application; `VmpControl.Tests` are its tests.
- `ServerTest`, `ServerPersistentSyncTest` and `LmpCommonTest` are the test projects of the server and the shared code.
- `Scripts` contains build, packaging, deployment and server management helpers.
- `docs` contains the documentation listed below.

Some internal names still say `Lmp`, `Luna` or `Kspmp`. They are kept so that the code stays close to its upstream; the visible product identity and the protocol and install separation use the VMP names.

## Requirements

- The .NET 10 SDK to build the server, the server tests and VmpControl. VmpControl and the build and server helper scripts are for Windows.
- KSP 1.12 with the `000_Harmony` mod for the client (the client zip contains it).
- A local KSP install to build the client, because the stock KSP managed DLLs are not repository content.

## Build And Test

Run this before the first full build on a new machine:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Scripts\BootstrapLocalBuild.ps1
```

It restores NuGet packages into the global cache, recreates the ignored `External/Nuget` compatibility folders that the legacy projects use, and copies the required KSP managed DLLs into `External/KSPLibraries` from a local KSP install. If KSP is not in a common location, pass it explicitly:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Scripts\BootstrapLocalBuild.ps1 -KspRoot "C:\Path\To\Kerbal Space Program"
```

Build and test commands:

```powershell
# Client and server into Build\Release (the server is published to Build\Release\Server)
.\Scripts\BuildOnly.ps1 -Configuration Release

# Server only, and the server tests (these also run in CI)
dotnet build Server/Server.csproj -c Release
dotnet test ServerTest/ServerTest.csproj -c Release
dotnet test ServerPersistentSyncTest/ServerPersistentSyncTest.csproj -c Release

# VmpControl is not part of VladMultiplayer.sln
dotnet test VmpControl.Tests/VmpControl.Tests.csproj
```

`External/KSPLibraries`, `External/Nuget` and `Scripts/SetDirectories.bat` are intentionally not committed. Use `Scripts/SetDirectories.example.bat` as the template for the last one. Reference repositories, local KSP installs and private test-server folders stay outside the repository.

## Installing The Client

Unzip `VladMultiplayer-client.zip` into the KSP root folder. After extraction, `GameData` should contain both `000_Harmony` and `VladMultiplayer`. Do not put the dedicated server files in `GameData`.

## Hosting A Dedicated Server

Unzip `VladMultiplayer-server.zip` outside the KSP folder.

- Windows: run `Server.exe`.
- Portable builds need the .NET 10 runtime: run `dotnet Server.dll` from the extracted `VMPServer` folder. Windows self-contained builds include the runtime. Screenshot sharing uses Windows System.Drawing; other platforms need a separate image backend before that feature is supported.

Settings live in `Config/vmp-server.json`. Edit the file while the server is stopped, or use the Settings page of VmpControl or the settings endpoints of the admin API, which validate the change and save it. The most relevant sections:

- `connection`: the game port, UDP `8800` by default, the packet size (1200 bytes, MTU auto-expansion off, to leave room for tunnel overhead) and UPnP (off by default).
- `website`: the status page, enabled on TCP `8900` by default.
- `masterServer`: whether the server registers with a master server.
- `adminApi`: the local admin API, enabled on port `18900` by default. Its token is stored in `Config/admin-token.txt`.
- `compression`, `interval`, `general`: compression mode and levels, backup intervals, and the server name, password and game mode.

For public hosting behind IPv4 CGNAT, use reachable IPv6 or a tunnel such as ZeroTier or playit; classic IPv4 port forwarding may not be possible. A playit tunnel must carry UDP; leave Proxy Protocol set to None. Master-server registration is discovery and does not guarantee successful NAT traversal.

For private hosting, use a server password, disable master-server listing, and keep the web/status listener disabled or bound to localhost. ZeroTier's Windows network profile is often Public, so allow the game on that profile with a rule limited to your overlay subnet and server program.

Helper scripts for a Windows host:

- `Scripts/Install-TrainServer.ps1` installs a server archive into a new folder after checking its SHA-256 and the server password, opens the firewall for the game port (`-LanSubnet` sets the local network range) and creates the scheduled tasks.
- `Scripts/Manage-VmpServer.ps1` provides Start, Stop, Restart, Status and Backup for the scheduled setup. Stop and Backup use `shutdown.request` to run the server's normal save path before exiting. Keep the server folder writable only by trusted local users.
- `Scripts/New-FriendPack.ps1` creates a private pack from an installed GameData tree with SHA-256 file manifests and a separate settings template. Stock KSP, DLC, player identity data and generated ModuleManager caches are excluded. Its installer (`Scripts/Install-FriendPack.ps1`) checks KSP and DLC, verifies hashes, backs up existing mods, sets a distinct player name and verifies the installed files. Each player supplies their own KSP and DLC installation.

## Master Servers

VMP discovery is backed by the repository. Clients, dedicated servers and master servers read the fork URLs in `LmpGlobal/RepoConstants.cs`, and they fall back to the shipped `MasterServersList/*.txt` copies if GitHub raw is temporarily unavailable.

Master-server binaries are not part of the normal releases. To run a public VMP browser, build the master server from source (a `Dockerfile_MasterServer` is included):

```powershell
dotnet msbuild .\MasterServer\MasterServer.csproj /p:Configuration=Release /p:Platform=AnyCPU
.\MasterServer\bin\Release\MasterServer.exe /noupdatecheck
```

It listens on UDP `8700` for server registration, server-list requests and NAT introduction, and on TCP `8701` for the web/json server list. Open both ports on the host or publish them from the Docker container. After the master server is reachable, add its `host:8700` entry to `MasterServersList/MasterServersList.txt`. Dedicated servers appear in the browser when `RegisterWithMasterServer` is true in the `masterServer` settings section and the server can reach a listed VMP master server.

Do not use public LunaMP master servers as VMP defaults. They do not preserve the VMP fields `ProtocolForkId` and `ExactSessionBuild`, so they cannot reliably advertise VMP-only features or guarantee compatible NAT introduction.

## Releases

Public releases contain runtime artifacts only: `VladMultiplayer-client.zip` for players and `VladMultiplayer-server.zip` for dedicated-server hosts. Test projects, local test-server data, private `.env` files, KSP managed DLL source folders and master-server binaries are not release assets.

`Scripts/PackageKspmpReleaseZips.ps1` builds the zips. `release.ps1` uploads them with the GitHub CLI (run `gh auth login` once):

```powershell
./release.ps1 -Version 0.2.0
./release.ps1 -Version 0.2.0 -SkipCommit   # the tag already exists; only rebuild or replace assets
```

The `release.yml` workflow runs on a self-hosted Windows runner, because the client build needs local KSP managed DLLs. Pushing a tag like `v0.1.0` bootstraps the dependencies, packages the zips, uploads them as workflow artifacts and publishes a GitHub release. Set the optional repository variable `VMP_KSP_ROOT` if the runner cannot detect the KSP install. The `ci.yml` workflow builds the server and runs the server tests on every push to `main`.

## Documentation

- [docs/admin-api.md](docs/admin-api.md): the local admin API, the token, the settings file and its import.
- [docs/compression.md](docs/compression.md): message compression, modes, levels and settings.
- [docs/diagnostics.md](docs/diagnostics.md): client health data, log bundles and host-requested reports.
- [docs/ksce-integration-notes.md](docs/ksce-integration-notes.md): launch pad coordination, inherited from Luna Multiplayer.
- [VmpControl/README.md](VmpControl/README.md): the tray application.
- [CONTRIBUTING.md](CONTRIBUTING.md) and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## License

MIT. See [LICENSE](LICENSE).
