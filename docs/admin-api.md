# VMP admin API: local JSON interface of the server

The server contains a small HTTP API that returns the state of the running server as JSON and offers five actions: create a
backup, broadcast a chat message, kick a player, ask a player's game to send its logs and read or change the server settings. It is meant for tools that run on the same machine as the server, such as
the VMP server manager, and for administrators who reach that machine through an SSH tunnel. It is separate from the public
status site (port 8900).

## Binding

* The API listens on `127.0.0.1` only. The address is fixed in the code (`IPAddress.Loopback`); there is no setting to change
  it, and it is IPv4 only. The port is never reachable from the network, so no router or firewall change is needed.
* Every request is checked a second time: the address of the connecting peer must be a loopback address (an IPv4-mapped IPv6
  address is unwrapped first). Any other peer receives `403`.
* The API is started together with the server and stopped when the server closes. The settings below are read once at start.
  The API stays off (the reason is written to the server log) when it is disabled, when the port is not between 1 and 65535,
  when the port is already in use, when the port cannot be bound, or when `admin-token.txt` holds a token that is too short.
* After a successful start the server log contains
  `[AdminApi] listening on 127.0.0.1:<port> (local only, bearer token required)`.

## Settings

The `adminApi` section of `Config/vmp-server.json` in the server folder (see "The settings file" below):

| Setting          | Default | Meaning                                                                                       |
|------------------|---------|-----------------------------------------------------------------------------------------------|
| `EnableAdminApi` | `true`  | `false` keeps the API off: no listener is opened and no token file is created.                |
| `Port`           | `18900` | TCP port on `127.0.0.1`. Valid values are 1 to 65535. The server must be restarted to change it. |

These two settings can be changed only by editing the file and restarting the server, not through `PUT /api/v1/settings`, so
that a wrong value can never lock the operator out of the API.

## Token file

* Name and location: `Config/admin-token.txt`, relative to `<server-folder>` (the folder of the server executable).
* Creation: when the API starts and the file does not exist, a new token is generated and written. An existing file is never
  replaced. The server log then contains `[AdminApi] created a new token in Config/admin-token.txt`.
* Format: 32 random bytes from the operating system random number generator, written as 64 lowercase hexadecimal characters
  in ASCII, with no trailing newline.
* File permissions: on systems other than Windows the file mode is set to owner read/write (best effort). On Windows the
  file inherits the permissions of the server folder.
* Reading: the file is read when the API starts and surrounding whitespace is trimmed. A file shorter than 32 characters is an
  error: the API stays off and the file is left unchanged. Delete the file and restart the server to get a new token. A token
  edited by hand is accepted if it has at least 32 characters.
* Checking: the server keeps only the SHA-256 hash of the token. For each request the SHA-256 hash of the presented token is
  compared with it using a fixed-time comparison.
* The token is never written to the server log. It is not part of server backups (an archive holds the universe, the `Config`
  XML files and a sanitized copy of `vmp-server.json`).

## Authentication

Every request needs the header

```
Authorization: Bearer <token>
```

* The scheme name is case-insensitive. The token is the rest of the header value with surrounding whitespace removed.
* A missing, empty, malformed or wrong token is answered with `401` and the body `{"error":"unauthorized"}`. The response
  never says which part was wrong.
* The token is accepted only in this header, never in the URL.
* A request with two `Authorization` headers is answered with `400`.

### Failed authentications

* The server counts failed authentications (a missing, malformed or wrong token). After 10 failures within 60 seconds,
  every request is answered with `429` and the body `{"error":"too many failed authentications, try again later"}` for the
  next 30 seconds. During that time the token is not checked at all, so even a correct token is refused. Afterwards a new
  60-second window starts.
* Requests from a non-loopback peer (`403`) are not counted.
* While this happens the server log gets at most one warning line per minute, for example
  `[AdminApi] 10 failed admin API authentications, 7 requests refused with 429 (requests are refused for 30 seconds after 10
  failures within 60 seconds)`. Tokens are never part of a log line.

## Conventions

* Request and response bodies are JSON in UTF-8. Response property names are camelCase. Property names in request bodies are
  matched case-insensitively. Responses are written on one line; the examples below are indented for readability.
* Properties that end in `Utc` are UTC timestamps in ISO 8601 form. Values that are not available are `null`.
* The path prefix is `/api/v1/`. Paths are case-sensitive and a trailing `/` is ignored. The method is case-insensitive.
  The query string is read only by `GET /api/v1/log`.
* Text that comes from players or from the log (player names, build strings, log lines, chat text, kick reasons) has control
  characters, including tab and line breaks, replaced by spaces.
* Each response carries `Content-Type: application/json; charset=utf-8`, `Cache-Control: no-store`,
  `X-Content-Type-Options: nosniff` and `Connection: close`. No CORS headers are sent.
* Limits of the HTTP layer: one request per connection, at most 16 connections at once (further connections are closed without
  an answer), 10 seconds to send the request and 10 seconds to receive the response, request headers up to 8192 bytes, request
  body up to 4096 bytes (65536 bytes for `PUT /api/v1/settings`), no chunked request bodies, `HTTP/1.x` request lines only.

## Endpoints

| Method | Path                | Purpose                                                       | Body     |
|--------|---------------------|---------------------------------------------------------------|----------|
| `GET`  | `/api/v1/status`    | Server version, uptime, resource use, players, settings, host | none     |
| `GET`  | `/api/v1/network`   | Traffic statistics and snapshot compression                   | none     |
| `GET`  | `/api/v1/clients`   | Every connection with traffic counters and latency            | none     |
| `GET`  | `/api/v1/log`       | Last lines of the current server log (`?lines=N`)             | none     |
| `GET`  | `/api/v1/reports`   | Index of the client log bundles stored on the server          | none     |
| `GET`  | `/api/v1/backups`   | Backup files in the `Backups` folder                          | none     |
| `POST` | `/api/v1/backups`   | Create an archive backup now                                  | none     |
| `POST` | `/api/v1/backups/prune` | Apply the backup retention now (or preview it)            | optional |
| `DELETE` | `/api/v1/backups/<fileName>` | Delete one backup file                           | none     |
| `GET`  | `/api/v1/settings`  | Every server setting with its description, limits and value   | none     |
| `PUT`  | `/api/v1/settings`  | Change one or more settings (all or none), then save          | required |
| `POST` | `/api/v1/say`       | Broadcast a chat message to all clients                       | required |
| `POST` | `/api/v1/kick`      | Disconnect a player                                           | required |
| `POST` | `/api/v1/clients/requestlogs` | Ask one player's game to send its logs              | required |

Status codes that every endpoint can return, in addition to the ones listed per endpoint: `401`, `403`, `405` (wrong method
for a known path), `413`, `429` (see "Failed authentications") and `500`. See "Errors" below.

### GET /api/v1/status

Request body: none.

```json
{
  "version": "0.2.0-train.20261023.10",
  "startedUtc": "2026-10-28T08:00:12.4Z",
  "uptimeSeconds": 14523.7,
  "workingSetBytes": 612368384,
  "cpuPercent": 3.2,
  "playersOnline": 2,
  "players": [
    { "name": "Player1", "subspace": 0, "connectedSinceUtc": "2026-10-28T11:13:40.6Z", "connectedSeconds": 2915.5 },
    { "name": "Player2", "subspace": 1, "connectedSinceUtc": "2026-10-28T11:50:02.3Z", "connectedSeconds": 733.8 }
  ],
  "vesselCount": 148,
  "universeBytes": 73400320,
  "settings": {
    "serverName": "Example VMP Server",
    "gameMode": "Career",
    "warpMode": "Subspace",
    "maxPlayers": 20,
    "passwordProtected": true,
    "cheats": false,
    "gamePort": 8800
  },
  "machine": {
    "ramTotalBytes": 34359738368,
    "ramFreeBytes": 18253611008,
    "osUptimeSeconds": 604800.5,
    "diskFreeBytes": 412316860416,
    "diskTotalBytes": 1000203091968,
    "diskRoot": "C:\\"
  }
}
```

* `version` is the full build string of the server assembly (`unknown` when none can be read).
* `cpuPercent` is the CPU time of the server process as a percentage of the whole machine, calculated between two requests.
  Requests less than one second apart return the previous figure.
* `players` lists authenticated players only. `universeBytes` is the total size of the universe folder and is cached for
  30 seconds.
* `settings` shows whether a password is set (`passwordProtected`), never the password. `gameMode` and `warpMode` are the
  names of the enum values.
* `machine` describes the PC that runs the server. `ramTotalBytes`, `ramFreeBytes` and `osUptimeSeconds` are read on Windows
  only (`null` elsewhere). `diskFreeBytes`, `diskTotalBytes` and `diskRoot` describe the drive that holds the server folder.
  Each figure is `null` when it cannot be read.

Status codes: `200`.

### GET /api/v1/network

Request body: none.

```json
{
  "startedUtc": "2026-10-28T08:00:12.4Z",
  "uptimeSeconds": 3725.3,
  "windowsRecorded": 3,
  "totals": { "sentBytes": 48211734, "sentMessages": 152330, "receivedBytes": 9120455, "receivedMessages": 98412 },
  "lastMinute": {
    "startUtc": "2026-10-28T09:01:00Z",
    "endUtc": "2026-10-28T09:02:00Z",
    "seconds": 60,
    "out": {
      "bytes": 780000, "messages": 2520, "bytesPerSecond": 13000,
      "types": [ { "type": "Vessel", "bytes": 520000, "messages": 1100, "bytesPerSecond": 8666.7 } ]
    },
    "in": {
      "bytes": 150000, "messages": 1630, "bytesPerSecond": 2500,
      "types": [ { "type": "Vessel", "bytes": 96000, "messages": 900, "bytesPerSecond": 1600 } ]
    },
    "snapshots": []
  },
  "lastTenMinutes": {
    "startUtc": "2026-10-28T08:59:00Z",
    "endUtc": "2026-10-28T09:02:00Z",
    "seconds": 180,
    "out": {
      "bytes": 2300000, "messages": 7400, "bytesPerSecond": 12777.8,
      "types": [ { "type": "Vessel", "bytes": 1530000, "messages": 3300, "bytesPerSecond": 8500 } ]
    },
    "in": {
      "bytes": 440000, "messages": 4800, "bytesPerSecond": 2444.4,
      "types": [ { "type": "Vessel", "bytes": 280000, "messages": 2700, "bytesPerSecond": 1555.6 } ]
    },
    "snapshots": []
  },
  "sinceStart": {
    "startUtc": "2026-10-28T08:00:12.4Z",
    "endUtc": "2026-10-28T09:02:17.7Z",
    "seconds": 3725.3,
    "out": {
      "bytes": 48211734, "messages": 152330, "bytesPerSecond": 12941.7,
      "types": [ { "type": "Vessel", "bytes": 31000000, "messages": 70000, "bytesPerSecond": 8321.5 } ]
    },
    "in": {
      "bytes": 9120455, "messages": 98412, "bytesPerSecond": 2448.2,
      "types": [ { "type": "Vessel", "bytes": 5400000, "messages": 52000, "bytesPerSecond": 1449.6 } ]
    },
    "snapshots": [
      {
        "domain": "Funds", "snapshots": 12, "rawBytes": 4800, "wireBytes": 1100, "ratio": 4.36,
        "encodings": { "raw": 2, "quickLz": 0, "zstd": 6, "zstdDictionary": 4 }
      }
    ],
    "messageCompression": [
      { "type": "VesselProtoMsgData", "messages": 410, "rawBytes": 6150000, "wireBytes": 1180000, "ratio": 5.21, "codec": "zstd", "avgLevel": 12.4, "savedBytes": 4970000 }
    ]
  },
  "snapshotCompression": [
    {
      "domain": "Funds", "snapshots": 12, "rawBytes": 4800, "wireBytes": 1100, "ratio": 4.36,
      "encodings": { "raw": 2, "quickLz": 0, "zstd": 6, "zstdDictionary": 4 }
    }
  ],
  "messageCompression": [
    { "type": "VesselProtoMsgData", "messages": 410, "rawBytes": 6150000, "wireBytes": 1180000, "ratio": 5.21, "codec": "zstd", "avgLevel": 12.4, "savedBytes": 4970000 }
  ],
  "compression": {
    "mode": "Adaptive", "serverLevel": 9, "effectiveLevelNow": 15, "clientLevel": 5,
    "cacheHits": 1840, "cacheMisses": 1020, "avgCompressMicrosPerKb": 21.5
  }
}
```

The example is shortened: the lists `types`, `snapshots` and `messageCompression` normally hold more entries.

* `out` is what the server sent, `in` what it received. `type` is the name under which the traffic statistics count the
  messages.
* `windowsRecorded` is the number of closed one-minute windows kept in memory (at most 10). `lastMinute` and `lastTenMinutes`
  are `null` until the first window has closed. `sinceStart` and `totals` cover the whole run of the server.
* `types` lists at most 25 entries per direction; `bytes` and `messages` of the direction cover all types.
  `bytesPerSecond` is rounded to one decimal place and `ratio` to two.
* `snapshotCompression` repeats the per-domain snapshot figures of `sinceStart`. `encodings` counts the snapshots by
  encoding.
* `messageCompression` (also present in `lastMinute`, `lastTenMinutes` and `sinceStart`) lists, per message type, how much
  the payloads that are compressed one by one shrink: `rawBytes` is the size before compression, `wireBytes` the size
  that is sent, `ratio` is `rawBytes / wireBytes` (0 when nothing was counted), `savedBytes` is `rawBytes - wireBytes`
  (never negative), `codec` the compressor: `zstd`, `quicklz` for payloads from a client that cannot compress with zstd,
  or `zstd+quicklz` when both occurred. `avgLevel` is the average zstd level of the compressions counted (0 when only
  decompressions were counted). Vessel, kerbal, scenario, contract and craft data are compressed one by one (see
  `compression.md`); the types are `VesselProtoMsgData`, `KerbalInfo`, `ScenarioInfo`, `ContractInfo` and `CraftInfo`.
  `messages` counts compressions (the server built the message, or took it from the cache) plus decompressions (the
  server read it), so a vessel that is received and forwarded counts twice. The list is empty until the first such
  payload; `messageCompression` is separate from the `snapshotCompression` figures of the persistent-sync snapshots.
* `compression` shows the settings in force and how they work out: `mode` (`Adaptive` or `Fixed`), `serverLevel`,
  `effectiveLevelNow` (the zstd level a typical 32 KB payload gets right now, after the timing feedback), `clientLevel`
  (the highest level clients are told to use), `cacheHits` and `cacheMisses` of the compressed payload cache since the
  start, and `avgCompressMicrosPerKb` (smoothed microseconds the server needs to compress 1 KB; 0 until something was
  compressed). The settings live in the `compression` section of `GET /api/v1/settings` and apply immediately.

Status codes: `200`.

### GET /api/v1/clients

Request body: none.

```json
{
  "clients": [
    {
      "name": "Player1",
      "authenticated": true,
      "subspace": 0,
      "connectedSinceUtc": "2026-10-28T11:13:40.6Z",
      "connectedSeconds": 2915.5,
      "bytesIn": 5231880,
      "bytesOut": 61244790,
      "wireBytesIn": 5688012,
      "wireBytesOut": 64310452,
      "build": "0.2.0-train.20261023.10",
      "latencyMs": 41.3
    },
    {
      "name": "",
      "authenticated": false,
      "subspace": 0,
      "connectedSinceUtc": "2026-10-28T12:02:15Z",
      "connectedSeconds": 1.1,
      "bytesIn": 0,
      "bytesOut": 0,
      "wireBytesIn": null,
      "wireBytesOut": null,
      "build": null,
      "latencyMs": null
    }
  ]
}
```

* The list contains every connection, including those that have not finished authenticating (`authenticated` is `false`).
* `bytesIn` and `bytesOut` count the serialized messages the server received from and sent to the client, before packet
  headers. `wireBytesIn` and `wireBytesOut` come from the network library and include headers and resends; they are `null`
  when unavailable.
* `latencyMs` is the average round-trip time in milliseconds, or `null` when no positive value is available.

Status codes: `200`.

### GET /api/v1/log

Query parameter: `lines`, a positive integer. Default `100`, maximum `500`; a larger value is reduced to `500`. Request body:
none.

```json
{
  "requested": 2,
  "count": 2,
  "lines": [
    "[12:00:01][Normal]: [AdminApi] listening on 127.0.0.1:18900 (local only, bearer token required)",
    "[12:02:10][Normal]: Broadcasting Server restarts in 5 minutes"
  ]
}
```

* `requested` is the number of lines asked for after the maximum was applied; `count` is the number returned, which is
  lower when the log is shorter. Lines are in file order, the newest last.
* The lines come from the file the server is writing to at the moment of the request. Only the last 512 KB of that file are
  read. A first line that is cut off by this limit is dropped. A missing file gives an empty list.

Status codes: `200`; `400` when `lines` is not a positive integer (including an empty value).

### GET /api/v1/reports

Request body: none.

```json
{
  "zipCount": 14,
  "count": 2,
  "entries": [
    { "file": "Player1_20261028-113500Z-stall.zip", "player": "Player1", "utc": "2026-10-28T11:35:00Z", "bytes": 482113, "reason": "stall" },
    { "file": "Player2_20261028-094210Z-crash.zip", "player": "Player2", "utc": "2026-10-28T09:42:10Z", "bytes": 391204, "reason": "crash" }
  ]
}
```

* `zipCount` is the number of `.zip` files in the `ClientLogs` folder.
* `entries` are the lines of `ClientLogs/index.jsonl` (one JSON object per stored upload), newest first, at most 100. Only
  the last 2 MiB of that file are read. A line that is not valid JSON is skipped. The objects are returned exactly as stored.
* The API reports which bundles exist; it does not return their content.

Status codes: `200`.

### GET /api/v1/backups

Request body: none.

```json
{
  "backups": [
    { "name": "Universe-20261028-120000.tar.zst", "bytes": 41943040, "createdUtc": "2026-10-28T12:00:04Z", "kind": "tar.zst" },
    { "name": "Universe-20261027-120000.tar.zst", "bytes": 41811968, "createdUtc": "2026-10-27T12:00:04Z", "kind": "tar.zst" }
  ],
  "count": 2,
  "totalBytes": 83755008,
  "capBytes": 2147483648,
  "policy": { "keepNewest": 10, "keepDailyDays": 14, "keepScreenshotArchives": 10, "legacyZipKeep": 0, "maxTotalMegabytes": 2048 }
}
```

* The list covers the files in the `Backups` folder whose names are recognized as backups, newest first (by file creation
  time), at most 500. `kind` is `tar.zst`, `zip` or `screenshots.tar`.
* `count` is the number of recognized backup files (not limited to 500). `totalBytes` is the size of **every** file in the
  folder (also `.sha256` sidecars, `.part` files and anything else), which is what the cap is measured against.
  `capBytes` is `interval.BackupMaxTotalMegabytes` in bytes, `0` = no cap.
* `policy` shows the retention settings in force: `keepNewest` (`interval.BackupKeepNewest`), `keepDailyDays`
  (`interval.BackupKeepDailyDays`), `keepScreenshotArchives` (`interval.BackupKeepScreenshotArchives`), `legacyZipKeep`
  (`interval.BackupLegacyZipKeep`, `0` = old `.zip` backups are not pruned by the keep rules) and `maxTotalMegabytes`.
* A missing `Backups` folder gives an empty list.

Status codes: `200`.

### POST /api/v1/backups

Creates a compressed archive backup now and then applies the backup retention settings. Request body: none (a body is
ignored). The response is sent when the backup has finished, so the connection stays open for the duration of the backup.

```json
{
  "fileName": "Universe-20261028-120000.tar.zst",
  "files": 1342,
  "rawBytes": 268435456,
  "compressedBytes": 41943040,
  "ratio": 6.4,
  "seconds": 8.3,
  "level": 6,
  "verified": true,
  "sha256": "9f2c1b7e4a8d03c65b1e7f0a2d94c8b36e5a1f7d0c4b8e92a3d6f1b05c7e8a24",
  "excluded": [],
  "skipped": [],
  "pruned": [ "Universe-20261020-120000.tar.zst" ]
}
```

* `pruned` lists the older backups that retention removed.
* Only one backup runs at a time. This includes the periodic backup of the server.

Status codes: `200`; `409` when a backup is already running; `500` when the backup failed (the message is
`backup failed: <reason>`).

### DELETE /api/v1/backups/&lt;fileName&gt;

Deletes one backup file and its `.sha256` sidecar. Request body: none. Query: `force=true` (see below).

```json
{ "name": "Universe-20261020-120000.tar.zst", "deleted": [ "Universe-20261020-120000.tar.zst", "Universe-20261020-120000.tar.zst.sha256" ], "freedBytes": 41943105 }
```

* The name must be **exactly** one of `Universe-<yyyyMMdd-HHmmss>.tar.zst`, `Universe-<yyyyMMdd-HHmmss>.zip`,
  `Screenshots-<yyyyMMdd-HHmmss>.tar` or the `.sha256` file of one of the first two: a valid date and time, upper and lower case
  as shown, no path separators, no `..`, no extra characters (not even a trailing space or newline, and no `%`-escapes, which
  are not decoded). Anything else is `400` and nothing is touched; the server never deletes a file whose name does not match.
* The newest `Universe-*.tar.zst` (and its sidecar) is protected: `409` unless `?force=true` is added.
* `409` while a backup is running, `404` when the file does not exist.
* The server log gets one line per deletion with the name and the bytes freed.

Status codes: `200`; `400` (not a backup file name); `404` (no such backup); `409` (a backup is running, the file is the
protected newest archive, or the file is in use by another program).

### POST /api/v1/backups/prune

Applies the backup retention settings now, or only shows what they would delete.

Request body (optional): `{ "dryRun": true, "includeLegacyZips": false }`. **A missing body or a missing `dryRun` means a dry run**:
files are only deleted when the request says `"dryRun": false`.

```json
{
  "deleted": [
    { "name": "Universe-20260901-100000.zip", "bytes": 7340032, "reason": "legacy-zip-keep" },
    { "name": "Universe-20261001-090000.tar.zst", "bytes": 41943105, "reason": "size-cap" }
  ],
  "freedBytes": 49283137,
  "dryRun": true,
  "totalBytesAfter": 2034567890,
  "capBytes": 2147483648,
  "stillOverCap": false,
  "failed": []
}
```

The rules, in this order (the same ones that run after every backup and at server start):

1. `Universe-*.tar.zst`: the newest `interval.BackupKeepNewest` are kept, plus the newest of each of the last
   `interval.BackupKeepDailyDays` days (`reason` `keep-rules`).
2. `Screenshots-*.tar`: the newest `interval.BackupKeepScreenshotArchives` are kept (`keep-rules`).
3. `Universe-*.zip` (the old script's backups): the newest `interval.BackupLegacyZipKeep` are kept, but only when that setting is
   above 0 (`legacy-zip-keep`). With `"includeLegacyZips": true` the rule is applied even when the setting is 0, by keeping the
   newest 3 zips (an explicit operator action).
4. The size cap `interval.BackupMaxTotalMegabytes` (0 = no cap) is measured against **every file** in the folder. While the
   folder is over the cap after rules 1-3, the oldest files go (`size-cap`): old `.zip` backups first (this is the only way the cap
   touches zips when `BackupLegacyZipKeep` is 0), then the oldest `Universe-*.tar.zst`, then the oldest `Screenshots-*.tar`. The
   newest `Universe-*.tar.zst` is never taken and at least 3 `Universe-*.tar.zst` always stay, so the folder can stay above the cap
   (`stillOverCap`).

A `.sha256` sidecar is deleted with its archive and counted in its `bytes`. Files that do not match the exact name patterns,
orphan sidecars and `.part` files are never touched (but count towards the size). The newest `Universe-*.tar.zst` is never deleted by
a prune. A file that is in use stays and is listed in `failed`.

Status codes: `200`; `400` (a body that is not a JSON object); `409` while a backup is running; `413` (body over 4 KB).

The same engine is available offline: `Server.exe prune [--dir <folder>] [--dry-run] [--include-legacy-zips]` (default folder
`Backups` next to the exe; the numbers come from `Config/vmp-server.json`, the defaults when it cannot be read; it prints one summary line
and then one file name per line; exit code 0, 1 on an error or when a file could not be deleted, 2 for bad arguments), and
`Server.exe backup --out <folder>` applies the same retention after it has written the archive (`--keep-newest` and `--keep-days`
override the two keep counts).

### POST /api/v1/say

Broadcasts a chat message to all connected clients, like the `say` command of the server console.

Request body:

```json
{ "text": "Server restarts in 5 minutes, please land." }
```

Response:

```json
{ "ok": true }
```

* `text` is required. Control characters are replaced by spaces and surrounding whitespace is removed. The result must not be
  empty and must not be longer than 500 characters.

Status codes: `200`; `400` for a missing or invalid body, a missing or empty `text`, or a `text` that is too long.

### POST /api/v1/kick

Disconnects a connected player, like the `kick` command of the server console.

Request body:

```json
{ "name": "Player2", "reason": "AFK in the VAB" }
```

Response:

```json
{ "ok": true }
```

* `name` is required, at most 64 characters. `reason` is optional, at most 200 characters; without a reason the player is
  told `No reason specified`. Both are cleaned and trimmed as for `say`.

Status codes: `200`; `400` for a missing or invalid body, a missing or empty `name`, or a `name` or `reason` that is too
long; `404` when no connected player has that name.

### POST /api/v1/clients/requestlogs

Asks one connected player's game to send a diagnostics bundle to the server, like the `requestlogs` command of the server
console. See `diagnostics.md` for what the bundle contains and where it is stored.

Request body:

```json
{ "name": "Player2", "note": "Please send the logs of the freeze" }
```

Response:

```json
{ "requested": true, "player": "Player2" }
```

* `name` is required, at most 64 characters. `note` is optional, at most 120 characters; it is shown in the info file of the
  bundle, and control characters are replaced as for `say`.
* The player's game sends the bundle only when the player allows automatic diagnostics in the VMP options; otherwise it
  only writes one line to its own log. A request that arrives while the game is loading is carried out afterwards.
* Nothing is sent to a game that is not the server's own build: the request message does not exist in older versions.
* The request is valid for 10 minutes and is used up by the upload it asks for. A requested upload is accepted even when
  `AutoLogUpload` is off, has its own budget of 10 per player per hour, and is subject to the size, disk and concurrency
  limits of every upload. The result appears in `GET /api/v1/reports` with the reason `requested`.

Status codes: `200`; `400` for a missing or invalid body, a missing or empty `name`, or a `name` or `note` that is too
long; `404` when no connected player has that name; `409` when the player's game is not the server's own build; `429` when
a request for the same player was made less than 60 seconds ago (the message says how long to wait).

### GET /api/v1/settings

Returns every server setting, grouped by section, with the information a settings editor needs. Request body: none.

```json
{
  "sections": [
    {
      "id": "general",
      "title": "General",
      "settings": [
        {
          "key": "general.ServerName",
          "label": "Server Name",
          "description": "Name of the server. Max 30 char",
          "type": "string",
          "value": "KSP Server",
          "default": "KSP Server",
          "min": null,
          "max": null,
          "maxLength": 30,
          "options": null,
          "restartRequired": false,
          "secret": false,
          "modified": false
        }
      ]
    }
  ]
}
```

* The sections are `general`, `connection`, `gameplay`, `interval`, `warp`, `craft`, `screenshot`, `log`, `debug`, `website`,
  `masterServer`, `dedicatedServer` and `adminApi`. The list is built from the settings classes of the server, so a setting that
  is added in a new version appears here without any change to this API.
* `key` is `<section id>.<setting name>`; the setting name is the property name used in `vmp-server.json`. `label` is the name
  split into words. `description` is the comment of the setting.
* `type` is `bool`, `int`, `double`, `string` or `enum`. For an `enum` the value is one of `options` (names, case is ignored when
  changing).
* `min`, `max` and `maxLength` are `null` when unbounded. Ports are 1 to 65535, other numbers are at least 0, byte values
  (the colour of the dedicated server window) are 0 to 255, and a text limit such as `Max 30 char` in the description becomes
  `maxLength`.
* `default` is the value a fresh installation would have. `modified` is `true` when `value` differs from it. For the
  `gameplay` section the default follows the difficulty chosen in `general.GameDifficulty`.
* `restartRequired` is `false` for settings that the server reads when it uses them (a change applies at once, for example to
  the next player who joins), and `true` for all others. See "The settings file" below.
* Secrets (names that contain `Password`, `Token`, `Secret` or `ApiKey`) are never returned: `value` and `default` are `null`,
  `secret` is `true` and `isSet` says whether a value exists.

Status codes: `200`.

### PUT /api/v1/settings

Changes settings in memory and saves `Config/vmp-server.json`. Request body (up to 64 KB):

```json
{ "changes": { "general.ServerName": "My server", "connection.Port": 8800, "general.Cheats": false } }
```

Response:

```json
{ "applied": ["general.ServerName", "connection.Port", "general.Cheats"], "restartRequired": ["connection.Port"], "saved": true }
```

* The request is validated completely first. Either all changes are applied or none: with a single wrong entry nothing is
  changed and nothing is saved. On success the file is saved once.
* A value must have the JSON type of the setting (`bool`: `true`/`false`; `int`: a whole number; `double`: a number; `string`:
  a string; `enum`: one of the `options`) and must respect `min`, `max` and `maxLength`.
* `restartRequired` lists the changed settings whose new value is used only after the server is restarted. They are saved and
  in effect after the next start; until then the server keeps working with the old value.
* Each changed setting writes one line to the server log, `[Settings] <key>: <old> -> <new> (changed through the admin API)`.
  Secrets are written as `***`.
* Settings of the `adminApi` section are refused with `403` (edit the file and restart the server instead).
* If the file cannot be written, the in-memory values are put back and the request fails (`500`). If the settings file could not
  be read at start (see below), nothing is changed and the request fails (`409`).

Status codes: `200`; `400` for a missing or invalid body, a missing or empty `changes` object, or invalid entries; `403` for
`adminApi.*`; `409`; `413` above 64 KB; `500`.

An invalid request is answered with the list of all problems:

```json
{
  "error": "settings rejected, nothing was changed: connection.Port (must be at most 65535); general.Bogus (unknown setting)",
  "errors": [
    { "key": "connection.Port", "message": "must be at most 65535" },
    { "key": "general.Bogus", "message": "unknown setting" }
  ]
}
```

## The settings file

All settings of the server are stored in one file, `Config/vmp-server.json` in the server folder. It replaces the thirteen
XML files (`GeneralSettings.xml`, `ConnectionSettings.xml`, `GameplaySettings.xml`, `IntervalSettings.xml`,
`WarpSettings.xml`, `CraftSettings.xml`, `ScreenshotSettings.xml`, `LogSettings.xml`, `DebugSettings.xml`,
`WebsiteSettings.xml`, `MasterServerSettings.xml`, `DedicatedServerSettings.xml`, `AdminApiSettings.xml`) of earlier versions.
The meaning of each setting did not change.

* Format: UTF-8 without a byte order mark, indented. One object per section; the section id is the camelCase name used by the
  API (`general`, `connection`, ...); the property names are the setting names; enums are written as their names.
* Reading is tolerant: a setting or a whole section that is missing gets its default, and unknown entries are ignored. After
  an update, settings that are new in that version are added to the file with their defaults at the next start.
* The server writes the file on start (only when its content changes) and after `PUT /api/v1/settings`. A write goes to a
  temporary file in the same folder, which then replaces `vmp-server.json`; the version it replaced is kept as
  `vmp-server.json.bak` (one generation). A crash during a write cannot leave a half-written file.
* The file holds the same credentials as the XML files did (`Password`, `AdminPassword`). Protect it like them.
* A hand edit is read at the next start. If the file cannot be read (invalid JSON, or a value of the wrong type), the server
  logs an error, runs with the defaults for what could not be read, and does not overwrite the file, so nothing is lost; saving
  is refused until the file is fixed (or removed) and the server is restarted.

### Start order and import of the old XML files

1. If `vmp-server.json` exists, it is read.
2. Otherwise, if any of the old XML files exists, they are imported once. Every XML file is read exactly as the earlier
   version did, so every old value is kept (a missing file gets its defaults). The result is written to a temporary copy,
   which is read back and compared with the XML values, every property of every section. Only if all are equal does the copy
   become `vmp-server.json`, and only then the XML files are moved (not deleted) to `Config/legacy-xml-<yyyyMMdd-HHmmss>/`
   (server local time). Files that are not settings files, such as `KSPModControl.xml`, are not touched. If a file cannot be read or any value
   differs, the import is aborted: an error is logged, nothing is written or moved, and the server runs on the XML values;
   settings cannot be saved until this is resolved.
3. Otherwise the defaults are used and `vmp-server.json` is created.

After a successful import the server log contains `Imported <n> settings file(s) from Config into Config/vmp-server.json ...`
with the name of the folder and the rollback steps.

### Rolling back to the XML files

Stop the server, delete `Config/vmp-server.json` (and `vmp-server.json.bak`), copy the files from
`Config/legacy-xml-<timestamp>/` back into `Config`, and start the previous version. With the XML files back in `Config` and no
`vmp-server.json`, this version imports them again.

### Settings that need a restart

`restartRequired` is `true` for every setting, except for these, which the server reads at the moment it uses them:

* `general.ServerName`, `general.ServerMotd`, `general.Password`, `general.AdminPassword`, `general.Cheats` and
  `general.AllowSackKerbals`;
* every setting of the `screenshot`, `craft` and `gameplay` sections;
* the backup settings of the `interval` section: `interval.ArchiveBackupIntervalMinutes`, `interval.BackupKeepNewest`,
  `interval.BackupKeepDailyDays`, `interval.BackupIncludeScreenshots`, `interval.BackupKeepScreenshotArchives`,
  `interval.BackupMaxTotalMegabytes` and `interval.BackupLegacyZipKeep` (read again for each backup and each prune).

A live change applies to the next time the value is used. Cheats, kerbal rules and the gameplay values are sent to a player when
the player joins, so players who are already connected keep the old values until they reconnect. The `gameplay` values are used
only while `general.GameDifficulty` is `Custom`; for another difficulty the server uses the values of that difficulty. If the
difficulty is not `Custom` and `gameplay` differs from it, the server switches the difficulty to `Custom` at the next start
(and logs that it did).

`general.MaxPlayers` needs a restart even though the player limit is checked on every join: the connection limit of the network
layer is copied from it when the server starts, so raising it takes effect only after a restart.

### Backups

An archive backup does not contain `vmp-server.json` itself. It contains `Config/vmp-server.json.sanitized`, a copy in which
every `Password`, `Token`, `Secret` and `ApiKey` value is blank, using the same rule as for the XML files. It contains neither
`vmp-server.json.bak` nor the `legacy-xml-*` folders (both hold the credentials), nor `admin-token.txt`. Restoring a backup
never writes over a live `Config/vmp-server.json`.

## Errors

Every error response has the same body, a single `error` property with a short message:

```json
{ "error": "unauthorized" }
```

| Status | `error` message                                                                                                    | Cause                                                          |
|--------|--------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------|
| `400`  | `bad request`                                                                                                      | Malformed request line or header, duplicate `Authorization` or `Content-Length` header |
| `400`  | `transfer-encoding is not supported`                                                                               | Request with a `Transfer-Encoding` header                      |
| `400`  | `lines must be a positive number`                                                                                  | Invalid `lines` parameter                                      |
| `400`  | `a JSON body is required`, `invalid JSON`, `a JSON object is required`                                             | Missing, unparsable or non-object body of `say` or `kick`      |
| `400`  | `text is required`, `text is too long`, `name is required`, `name is too long`, `reason is too long`               | Field validation of `say` and `kick`                           |
| `400`  | `settings rejected, nothing was changed: ...` (with an `errors` list), `a "changes" object is required, ...`, `changes must contain at least one setting` | Invalid `PUT /api/v1/settings` request |
| `401`  | `unauthorized`                                                                                                     | Missing or wrong token                                         |
| `403`  | `forbidden`                                                                                                        | Peer address is not a loopback address                         |
| `403`  | `the adminApi settings can not be changed through the API; ...`                                                    | `PUT /api/v1/settings` with a key of the `adminApi` section    |
| `404`  | `not found`                                                                                                        | Unknown path (including paths outside `/api/v1/`)              |
| `404`  | `player not found`                                                                                                 | `kick` target is not connected                                 |
| `404`  | `Player not found or not connected: <name>`                                                                        | `requestlogs` target is not connected                          |
| `409`  | `The game of <name> is not the same build as the server, ...`                                                      | `requestlogs` target runs another build and cannot be asked    |
| `429`  | `Logs were already requested from <name> a moment ago. Try again in <n> s.`                                        | `requestlogs` for the same player less than 60 seconds ago     |
| `400`  | `note is too long (at most 120 characters)`                                                                        | `requestlogs` note above 120 characters                        |
| `405`  | `method not allowed`                                                                                               | Known path, wrong method                                       |
| `409`  | `a backup is already running`                                                                                      | `POST /api/v1/backups` while a backup runs                     |
| `409`  | `a backup is running, try again when it has finished`                                                              | `DELETE /api/v1/backups/<fileName>` or `POST /api/v1/backups/prune` while a backup runs |
| `409`  | `this is the newest archive backup and is protected; add ?force=true to delete it anyway`                           | `DELETE /api/v1/backups/<fileName>` of the newest archive      |
| `400`  | `not a backup file name; expected ...`                                                                              | `DELETE /api/v1/backups/<fileName>` with a name that is not exactly a backup file name |
| `404`  | `no such backup`                                                                                                   | `DELETE /api/v1/backups/<fileName>` of a file that is not there |
| `409`  | `the settings could not be saved because the settings file was not readable at start, so nothing was changed`      | `PUT /api/v1/settings` while saving is refused                 |
| `413`  | `request body too large`                                                                                           | Body or `Content-Length` above 4096 bytes (65536 for `PUT /api/v1/settings`) |
| `429`  | `too many failed authentications, try again later`                                                                 | 10 failed authentications within 60 seconds; lasts 30 seconds  |
| `431`  | `request headers too large`                                                                                        | Request headers above 8192 bytes                               |
| `500`  | `internal error`                                                                                                   | Unexpected failure; the detail is written to the server log, not returned |
| `500`  | `backup failed: <reason>`                                                                                          | The archive backup failed                                      |
| `500`  | `the settings could not be saved, nothing was changed`                                                             | The settings file could not be written; the values were put back |

Order of checks: the peer address is checked first (`403`). A malformed or oversized request is then answered by the HTTP
layer (`400`, `413`, `431`) before the token is looked at. For all other requests the rate limit (`429`), the token (`401`),
the body size (`413`) and the route (`404`, `405`) are checked, in this order.

## Using the API from another PC through an SSH tunnel

The API cannot be reached from the network, but an SSH tunnel makes it available on the local PC. SSH connects to
`127.0.0.1` on the server machine, so the server sees a loopback peer.

1. Open the tunnel and keep it running (`<ssh-alias>` is an SSH host entry or `user@host`, `<port>` is the `Port` setting):

   ```
   ssh -N -L <port>:127.0.0.1:<port> <ssh-alias>
   ```

2. Read the token on the server machine (`<server-folder>` is the folder of the server executable):

   ```
   ssh <ssh-alias> cat <server-folder>/Config/admin-token.txt
   ```

   Any command that prints the file works; use the print command of the shell of the server machine.

3. Call the API through the local end of the tunnel:

   ```
   curl -H "Authorization: Bearer <token>" http://127.0.0.1:<port>/api/v1/status
   curl -H "Authorization: Bearer <token>" "http://127.0.0.1:<port>/api/v1/log?lines=50"
   curl -X POST -H "Authorization: Bearer <token>" -H "Content-Type: application/json" \
        -d '{"text":"Server restarts in 5 minutes"}' http://127.0.0.1:<port>/api/v1/say
   ```

If the local port is already in use, forward another local port (`-L <local-port>:127.0.0.1:<port>`) and use that port in the
URLs.

## Security notes

* What the API can do: read the server status, traffic statistics, the connection list, the tail of the server log, the index
  of client log bundles and the list of backups; create an archive backup; broadcast a chat message; kick a player; ask a player's game to send its logs; read the
  settings and change them (except the `adminApi` section).
* What the API cannot do: restart or stop the server, change the `adminApi` settings, or read or write files. These routes
  do not exist: requests for paths such as `/api/v1/restart`, `/api/v1/stop` or `/api/v1/files` get `404`. Passwords are never
  returned; `status` and `settings` only say whether one is set. A caller with the token can set a new password.
* The log tail returns the text of the server log, with control characters replaced. Treat it as sensitive.
* The token grants the full API. Keep `Config/admin-token.txt` readable only by the account that runs the server (on Windows
  the file has the permissions of the server folder). To replace a token, stop the server, delete the file and start the
  server; the new token is written to the same file. Do not put the token in URLs, scripts that are shared, or chat.
* The token is the only credential. Failed attempts are limited (10 per minute, then 30 seconds of `429`) and summarised in
  the server log at most once a minute, without any token.
* Do not expose the API publicly. It speaks plain HTTP without TLS, so the token and all data would travel unprotected, and
  a single static token with a short lockout is not enough protection against guessing from the internet. The code protects the
  API by binding to `127.0.0.1` and refusing non-loopback peers; a reverse proxy, port forward or other program on the server
  machine that relays remote requests to `127.0.0.1` makes them look local and removes that protection, leaving only the
  token. Reach the API through an SSH tunnel instead.
* No CORS headers are sent, so a web page cannot read responses of the API from another origin.
