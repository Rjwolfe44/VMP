# VMP diagnostics: logs that come to the host

Clients can send a diagnostics bundle to the server, by hand or automatically, and the host reads them with
`fetch-client-logs.ps1` and `tools\vmp-logdoctor`.

## What a client records

* `KSP.log` has a `[VMP]: Health 60s ...` line every minute: scene, fps (average / worst / 1% low), frame stalls, longest
  frame, errors and exceptions, garbage collections, managed heap, working set, private bytes, free RAM, page faults and
  scene changes. The same numbers go to `GameData\VladMultiplayer\logs\health.jsonl` (kept to about 256 KB).
* `[VMP]: Frame stall N s in SCENE. <memory>` for every frame of 1.5 s or more, and `[VMP]: Slow operation: ...` for
  the known expensive operations (game saves after vessel loads, etc.).
* `logs\session.marker` exists while the game runs and is removed on a clean exit. If it is still there next launch the
  previous session crashed or was killed.

## What a bundle contains

`info.txt` (reason, trigger, build, machine, server, scene, a digest of the health data: memory now, recent stalls, last
health rows, most frequent errors), `KSP.log`, `Player.log`, `Player-prev.log` (the run before), `ApexGuidance.log`,
`ApexGuidance\Logs\*` (flight telemetry CSVs and Apex diagnostics, newest 6 files), `VladMultiplayer\logs\health.jsonl`.
The Windows user name in paths is replaced with `<user>`. VMP settings files (server passwords) are never included.

## When a client sends one

* **Manual**: Options > "Send logs to host".
* **Automatic** (Options > "Send reports to the host automatically", on by default):
  * `stall`: a freeze of 8 s or more, or 3 freezes of 1.5 s or more within 5 minutes. Freezes while LOADING or in the main
    menu, and within 25 s of a scene change, do not count.
  * `errors`: 300 non-noise errors or 100 exceptions within a minute, counted from 25 s after the last scene change.
  * `crash`: the previous session did not exit cleanly (sent once, after joining).
* At least 20 minutes between automatic reports, at most 8 a day per client (remembered across restarts in
  `logs\auto-reports.txt`).
* The server refuses more than 2 automatic reports per player per hour and 8 per day (separate from the 3 manual ones per
  hour), 30 uploads per hour server-wide.

## Host controls

* Server setting `AutoLogUpload` (GeneralSettings.xml, default true): false refuses every automatic report. Manual sends
  still work.
* Stored in the server's `ClientLogs` folder as `<player>_<yyyyMMdd-HHmmss>Z[-stall|-errors|-crash|-requested|-test].zip`
  (newest 80 files / 600 MB kept) with one JSON line per upload in `ClientLogs\index.jsonl` (kept after the zips rotate).

## Reading them

```
.\fetch-client-logs.ps1 -List
.\fetch-client-logs.ps1 -Analyze        # download new bundles and write ClientLogs\reports\*.md and INDEX.md
.\fetch-client-logs.ps1 -AnalyzeAll     # re-analyse everything already downloaded
python -I tools\vmp-logdoctor\logdoctor.py <zip | KSP.log | folder> --out report.md
```

See `tools\vmp-logdoctor\README.md` for how to read the report.

## Testing the pipeline

Set the environment variable `VMP_DIAG_TEST` to `stall`, `errors`, `crash` or `test` before starting KSP: once the client
has joined a server and left the loading screen it sends one report with that reason (the client's own limits are
ignored; the server's still apply). While the variable is set and the report has not gone out, the log says why
(`The VMP_DIAG_TEST test report is waiting: ...`).
