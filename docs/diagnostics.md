# VMP diagnostics: logs that come to the host

Clients can send a diagnostics bundle to the server, by hand, automatically or when the host asks for one. The server
stores the bundles in its `ClientLogs` folder (see "Reading them").

## What a client records

* `KSP.log` has a `[VMP]: Health 60s ...` line every minute: scene, fps (average / worst / 1% low), frame stalls, longest
  frame, errors and exceptions, garbage collections, managed heap, working set, private bytes, free RAM, page faults and
  scene changes. The same numbers go to `GameData\VladMultiplayer\logs\health.jsonl` (kept to about 256 KB).
  Appended at the end of the line (older fields keep their places):
  `vmpMs=AVG/WORST top=System:ms,System:ms,System:ms clockSteps=N clockErrMs=N|na timeScale=MIN-MAX maxDt=S`.
  * `vmpMs`: VMP's own cost in ms per rendered frame over the minute (average / worst frame), timed with
    `Stopwatch.GetTimestamp` around every system's FixedUpdate, Update and LateUpdate in `SystemsHandler` (system routines
    included). `top` is the 3 most expensive systems by average ms per frame. Not counted: Harmony patches, KSP/GameEvents
    handlers and TimingManager callbacks registered by systems, message threads; so it is a lower bound of VMP's cost.
  * `clockSteps`: "Step clock sync" jumps in the minute (each is a visible hitch); `clockErrMs`: the last game-minus-server
    clock error (negative = game behind; `na` while time sync is not running); `timeScale`: the Time.timeScale range seen
    (0 = paused); `maxDt`: Time.maximumDeltaTime now (KSP's max physics step per frame, see "Clock catch-up" below).
  * In `health.jsonl` the same as `"vmpMs":{"avg","worstFrame","top":[{"system","ms","worstCallMs"}]}` and
    `"clock":{"steps","errMs" (null when na),"timeScaleMin","timeScaleMax","maxDt"}`. The report digest (`info.txt`) also
    has the last minute's numbers in words, with each top system's worst single call.
* `[VMP] Clock catch-up: max physics step per frame 0.040 -> 0.065 s (game 1.8 s behind, frame 55 ms)` and
  `... restored to 0.040 s (reason)` when the clock catch-up changes the cap (see below).
* `[VMP]: Frame stall N s in SCENE. <memory>` for every frame of 1.5 s or more, and `[VMP]: Slow operation: ...` for
  the known expensive operations (crew assignment check after vessel loads, scenario sends, vessel loads, etc.).
* `[VMP]: Slow operation: Game.Updated (game state save) took N ms in SCENE; ...` when a KSP game state save (every stock
  save, quicksave and scene-change save) takes 2 s or more, at most once a minute. It lists the scenario module saves
  (count, total, the 3 slowest as `Type (assembly) N ms`), the `onGameStateSave` and `onProgressNodeSave` handlers (count,
  total, the 3 slowest as `Type.Method (assembly) N ms`) and the rest (flight state capture, crew check, stock code).
* `[VMP]: Silenced stock 'Failed to add Resource' log lines (suppressed N so far)`, at most once a minute: a Harmony prefix on `PartSet.AddPartResource` skips the stock `[PartSet]: Failed to add Resource ...` line (about 104,000 per launch of a big Interstellar Fuel Switch vessel); behaviour is otherwise stock, and N is the running count.
* `logs\session.marker` exists while the game runs and is removed on a clean exit. If it is still there next launch the
  previous session crashed or was killed.

## What a bundle contains

`info.txt` (reason, trigger, build, machine, server, scene, a digest of the health data: memory now, recent stalls, last
health rows, most frequent errors), `KSP.log`, `Player.log`, `Player-prev.log` (the run before), `ApexGuidance.log`,
`ApexGuidance\Logs\*` (flight telemetry CSVs and Apex diagnostics, newest 6 files), `VladMultiplayer\logs\health.jsonl`.
The Windows user name in paths is replaced with `<user>`. VMP settings files (server passwords) are never included.

## When a client sends one

* **Manual**: Options > "Send logs to host". It can be used at any time, as often as needed (30 times an hour per player by
  default, 10 seconds apart). While it works the button shows "Sending logs 37%" and the line under it the result ("Sent" or
  "Failed: <reason>. Will retry in 2 minutes."). The game keeps running; see "Uploads that survive a freezing game".
* **Automatic** (Options > "Send reports to the host automatically", on by default):
  * `stall`: a freeze of 8 s or more, or 3 freezes of 1.5 s or more within 5 minutes. Freezes while LOADING or in the main
    menu, and within 25 s of a scene change, do not count.
  * `errors`: 300 non-noise errors or 100 exceptions within a minute, counted from 25 s after the last scene change.
  * `crash`: the previous session did not exit cleanly (sent once, after joining).
* **Requested by the host** (`requested`): see "Asking a player for logs" below.
* At least 20 minutes between automatic reports, at most 8 a day per client (remembered across restarts in
  `logs\auto-reports.txt`).
* The server refuses more than 2 automatic reports per player per hour and 8 per day (separate from the manual ones, which
  are limited to 30 per player per hour, and from the host-requested ones), and 300 uploads of any kind per hour server-wide.
  At most two uploads run at once, and a player's uploads are at least 10 seconds apart (counted from the end of the previous
  one, for every reason). These numbers are the defaults of the host settings below; the automatic budgets are fixed.

## Host controls

* Server setting `AutoLogUpload` (`general` section of `Config/vmp-server.json`, default true): false refuses every automatic report. Manual sends
  still work, and so do reports the host asked for.
* Stored in the server's `ClientLogs` folder as `<player>_<yyyyMMdd-HHmmss>Z[-stall|-errors|-crash|-requested|-test].zip`
  (newest 300 files / 2 GB kept, oldest deleted first) with one JSON line per upload in `ClientLogs\index.jsonl` (kept after
  the zips rotate).
* Server settings in the `general` section (all editable through the admin API and the server manager, and read again before
  every upload, so no restart is needed; values outside the range are clamped):

  | Setting | Default | Range | Meaning |
  | --- | --- | --- | --- |
  | `LogUploadsPerPlayerPerHour` | 30 | 1-1000 | manual uploads per player per hour |
  | `LogUploadMinSecondsBetween` | 10 | 0-3600 | wait between two uploads of one player |
  | `LogUploadsPerHourServerWide` | 300 | 1-10000 | uploads of every kind per hour |
  | `LogUploadFolderMaxFiles` | 300 | 1-100000 | zips kept in `ClientLogs` |
  | `LogUploadFolderMaxMegabytes` | 2048 | 1-1048576 | megabytes of zips kept in `ClientLogs` |

  The size limit (25 MB per bundle), the two concurrent uploads, the strict chunk order, the zip and exact-size checks, the
  automatic and host-requested budgets and the player-name sanitising are not settings.

## Uploads that survive a freezing game

A game that runs out of memory can freeze for a minute or more at a time. Log uploads are built to finish anyway:

* The client sends the zip from its own thread in 64 KB chunks (the largest the server takes), at most 256 KB/s and only
  while the previous chunk is mostly acknowledged. Nothing depends on the frame rate: a freeze delays the next chunk and the
  upload carries on afterwards. The sender waits up to 5 minutes for an acknowledgement before it gives up.
* The server drops an upload that receives no chunk for 10 minutes (it used to be 2). The whole upload may last 10 minutes plus
  the size at 16 KB/s (about 16 minutes for 5.7 MB, about 37 for 25 MB), never more than 60 minutes.
* **Retry.** When an upload fails because it timed out, the server was busy or the last upload was too recent, or because the
  connection was lost, the client tries again after 2 minutes and once more after 10 minutes (it keeps waiting while the player
  is off a server and drops the retry 15 minutes after it was due, or at once if the player turned automatic reports off or the
  game is connected to a different server by then). A disconnect while the client waits for the server's confirmation (every
  chunk sent) is not retried, because the upload may have arrived. A retry sends the zip that is already packed (it packs again
  only if the file is gone) as a new upload with the same reason; the status line counts down ("Will retry in 2 minutes").
  Failures that would repeat (too big, too many today, not a zip, not allowed, cancelled) are not retried, nor is an upload the
  server did not confirm (it may have arrived). A new click on the button, or a new automatic report, replaces a waiting retry.
* **Counting.** The client counts an automatic report once, when it is triggered (`logs\auto-reports.txt`), not per attempt.
  The server does not count an attempt that timed out, was dropped with the connection or was replaced by the player starting
  over, but only if at least one chunk had arrived (an upload that never delivered anything stays counted, so Begin-and-wait is
  no free loop) and at most 3 such refunds per player per hour; a cancelled upload or a refused file stays counted. A timed-out upload the host requested re-opens the request, so the
  retry is accepted. A new Begin from a player whose earlier upload has been silent for 30 seconds replaces that upload (its
  late chunks are ignored).
* Compatibility: the protocol is unchanged. A new client talks to an older server (which accepts 1-64 KB chunks and a new
  Begin after a timeout) but there the retry counts against the old, lower limits; an older client keeps working with the new
  server.

## Asking a player for logs

The host can ask one connected player's game to send its logs, for example after that player reports a freeze:

* Server console: `requestlogs <player> [note]`.
* Admin API: `POST /api/v1/clients/requestlogs` with `{"name":"<player>","note":"<optional>"}` (see `admin-api.md`).
  The VMP server manager has a "Request logs" action on its Actions tab.

What happens:

* The server sends the player's game a request message (new message type `Request` in the log upload family) with an id and
  an optional note of at most 120 characters. Control characters are removed. Nothing is sent to a game that is not the
  server's own build, because older versions cannot read the message; the API answers `409` and the console prints why.
* The game acts on it only when the player has left Options > "Send reports to the host automatically" on. Otherwise it
  writes one line to its log and sends nothing. When it sends, it shows the on-screen message "The server host asked for
  your game logs, sending them now." and uploads a normal bundle with the reason `requested`; the info file in the bundle
  says "requested by the host" and shows the note. A request that arrives during the loading screen or in the main menu waits
  until the game is usable. The game ignores a request while an upload is running and honours at most one request per
  minute. The 20 minute and 8 per day limits for automatic reports do not apply.
* The server accepts a `requested` upload only while a request for that player is pending: it was made less than 10 minutes
  ago and no upload has used it yet. The request is used up when the upload begins. An upload with the reason `requested`
  without a pending request is refused (`NotAllowed`). The `AutoLogUpload` setting does not block it, because the host asked
  for it. It has its own budget of 10 per player per hour, separate from the manual and the automatic ones; the size, disk
  and concurrency limits apply as for every upload.
* The server issues at most one request per player every 60 seconds.
* The stored file is named `<player>_<yyyyMMdd-HHmmss>Z-requested.zip` and appears in `ClientLogs\index.jsonl` with the
  reason `requested`.

## Reading them

Each bundle is an ordinary zip file in the `ClientLogs` folder of the server. Open `info.txt` first: it holds the reason,
the trigger, the build, the machine and a digest of the health data. `ClientLogs\index.jsonl` lists every upload with one
JSON line. The Reports page of the VMP server manager (`VmpControl/README.md`) and `GET /api/v1/reports` of the admin API
(`admin-api.md`) show the same index. Tools that analyse the bundles are not part of this repository.

## Time sync and clock catch-up

`TimeSyncSystem` keeps the game clock on the server clock. Errors from 25 ms to 3.5 s are corrected by skewing
Time.timeScale between 0.85 and 1.20; larger errors are fixed with a "Step clock sync" (Planetarium.SetUniversalTime,
a visible hitch).

At a low frame rate that is not enough: KSP's setting `PHYSICS_FRAME_DT_LIMIT` (Unity Time.maximumDeltaTime, 0.04 s by
default) caps game time per rendered frame, so at 52 ms frames the game runs at about 77% of real time whatever the
timeScale, falls behind and is stepped every 15-30 s. The clock catch-up (`LmpCommon/Sync/ClockCatchUpPolicy.cs`, glue in
`TimeSyncSystem.UpdateClockCatchUp`) raises the cap while all of these hold: the game is more than 0.3 s behind, the
smoothed frame time is above 90% of the cap (the cap is what holds the game back) and below 0.09 s (above that physics
already dominates and more physics per frame would make frames slower still). The raised cap is frame time + 10 ms rounded
up to 5 ms, between the player's setting and 0.08 s. It goes back to the player's setting once the game is less than 0.1 s
behind, when it is ahead, while warping or paused, on a scene change, when time sync stops, or when the option is turned
off; when it was undone because frames got physics-bound it is not raised again for 30 s. A player whose own setting is
already 0.08 s or more is never touched. The step sync stays as the fallback for errors over 3.5 s (load freezes). Option:
"At a low frame rate, let the game catch up with the server clock" in the VMP options (`ClockCatchUp` in the settings
file, on by default). Watch `clockSteps` and `maxDt` on the health line to see whether it helps.

Unverified in game: covered by unit tests of the policy only. Not yet confirmed: that KSP does not reset
Time.maximumDeltaTime by itself during a scene (the client re-applies its value every frame if it does, without logging
again), and how Unity combines the cap with a timeScale above 1 (if the cap applies after the scale, the 10 ms headroom
can still leave a little of the error to the timeScale skew).

## Testing the pipeline

Set the environment variable `VMP_DIAG_TEST` to `stall`, `errors`, `crash` or `test` before starting KSP: once the client
has joined a server and left the loading screen it sends one report with that reason (the client's own limits are
ignored; the server's still apply). While the variable is set and the report has not gone out, the log says why
(`The VMP_DIAG_TEST test report is waiting: ...`).
