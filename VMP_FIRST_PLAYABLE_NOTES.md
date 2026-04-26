# VMP First Playable Notes

This fork starts from KSPMulti because it already carries the LMP-style client/server experience, persistence hardening, IPv6-capable Lidgren UDP networking, and targeted compression for bulky game-state blobs.

## Decisions in this pass

- Product identity is now Vlad Multiplayer / VMP.
- Client install layout is `GameData/VladMultiplayer`.
- Lidgren application id is `VMP`, so VMP clients and servers are isolated from LMP/KSPMulti sessions.
- GitHub update checks are disabled until VMP has its own repository and releases.
- Internal `Lmp*`, `Luna*`, and script names are mostly kept to reduce first-build risk.
- No zstd or broad packet compression was added. The inherited targeted QuickLZ blob compression remains in place.

## First friend-test target

1. Build the dedicated server.
2. Build/package the client into `GameData/VladMultiplayer`.
3. Test localhost/direct LAN connection first.
4. Test public IPv6 if reachable from friends.
5. Use ZeroTier or playit when Starlink IPv4 CGNAT blocks inbound access.

## Current validation

- Client build passes: `dotnet build .\LmpClient\LmpClient.csproj -c Debug`.
- Server build passes: `dotnet build .\Server\Server.csproj -c Debug`.
- Full solution build passes: `dotnet build .\VladMultiplayer.sln -c Debug`.
- Common tests pass: `dotnet vstest .\LmpCommonTest\bin\Debug\LmpCommonTest.dll`.
- Server tests pass: `dotnet test .\ServerTest\ServerTest.csproj -c Debug`.
- Persistent sync tests pass: `dotnet test .\ServerPersistentSyncTest\ServerPersistentSyncTest.csproj -c Debug`.
- Debug staging passes: `.\Scripts\BuildOnly.bat Debug`.
- Debug zip packaging passes: `powershell -NoProfile -ExecutionPolicy Bypass -File .\Scripts\PackageKspmpReleaseZips.ps1 -Configuration Debug`.
- Current Debug artifacts are in `Build\Debug\artifacts\VladMultiplayer-Client-Debug.zip` and `Build\Debug\artifacts\VladMultiplayer-Server-Debug.zip`.
- Expected inherited warnings remain around `net5` end of support, old vulnerable packages, Lidgren XML docs, and master-server threading analyzers.

## Next technical work

- Add adaptive vessel update rates after the baseline build runs.
- Add better network diagnostics for latency, packet loss, queue depth, and vessel ownership.
- Add friend-test scripts for repeatable client/server packaging.
- Keep protocol changes gated by a VMP-only fork id so old clients fail clearly instead of corrupting sessions.