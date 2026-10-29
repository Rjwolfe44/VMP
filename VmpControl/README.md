# VmpControl

VmpControl is a Windows tray and desktop application for monitoring and operating a VMP game server. It reads server state and performs a few actions through the server's local admin API (see the [admin API reference](../docs/admin-api.md)), and it runs a PowerShell control script for the operations the API does not offer (start, stop, restart). It can run on the server PC itself (host mode) or on another PC that reaches the server over SSH (remote mode).

## Features

The application lives in the notification area. Closing the window hides it to the tray; the tray menu entry Exit ends the process. Only one instance runs per user session: starting a second instance asks the first one to show its window and then exits.

**Tray**

- The icon color shows the state: grey (connecting), red (server down or unreachable), blue (running, nobody online), green (running, players online). The tooltip reads `<server name> - <n> online - up <duration>`.
- Left click opens the window. The menu offers Open, Start, Stop, Restart, Back up now, Settings..., Start with Windows and Exit.
- Balloon notifications (can be switched off in the settings): `<player> joined`, `<player> left`, `Server went down` (after two consecutive polls that report the server as down, and only if the server was seen running before) and `Server is back`. Join and leave messages require a working admin API.

**Window pages**

| Page | Content |
| --- | --- |
| Overview | Server name, game mode and warp mode, version, uptime, server CPU and memory, vessel count, universe size; server PC RAM in use and free, free disk space, PC uptime; table of online players (name, subspace, connected for, since). Without the admin API the player names reported by the control script are shown instead. |
| Network | Traffic for the last minute, the last 10 minutes or since start: sent and received bytes, messages and bytes per second per message type, snapshot compression per domain (snapshots, raw size, sent size, ratio, encodings), a small send/receive rate graph, and a per-client table (bytes in and out, wire bytes in and out, latency, client build). Refreshes every 5 seconds while visible. |
| Logs | Tail of the server log (100, 200 or 500 lines), case-insensitive text filter, pause and resume, manual refresh. Lines containing `[Error]` or `[Fatal]`, `[Warning]` and `[Debug]` are colored. Refreshes every 3 seconds while visible. |
| Backups | List of existing backups (name, size, creation time, kind) and a "Back up now" button. The result line shows file name, raw and compressed size, ratio, duration, file count and whether the backup was verified, followed by the items left out, skipped or pruned. |
| Reports | List of client reports stored on the server (time, player, reason, size, file name). In remote mode the "Fetch and analyze..." button appears when the reports script is configured and exists (see `FetchLogsScriptPath`). |
| Actions | Start, Stop and Restart of the server; "Say to everyone" (server chat message); "Kick a player" with an optional reason; in remote mode also "Restart server PC..." and "Abort restart"; an activity table of everything the application did in this run. |
| Settings | View and change the server's settings (`Config/vmp-server.json`) through the admin API: sections on the left, one row per setting on the right, search, a "Modified only" filter, and a Save / Discard bar. See [Settings page](#settings-page). |

Stop, Restart and Kick ask for confirmation (Stop and Restart name the players who would be disconnected). Restarting the server PC requires typing `RESTART`. Say and Kick need the admin API. Start, Stop, Restart and the fallback backup go through the control script.

**Polling.** The state is polled every 2 seconds while the window is visible and every 10 seconds while it is hidden (`status`, then `clients`). While the admin API does not answer, the control script's `Status` action is used as a fallback to decide between "server down" and "API unreachable"; it is asked right after the API stops answering and otherwise at most every 20 seconds (60 seconds while the window is hidden).

## Settings page

The page reads `GET /api/v1/settings` (see the [admin API reference](../docs/admin-api.md)) and shows every setting of the server, grouped in the sections the server reports (for example General, Connection, Gameplay, Interval, Warp, Admin Api). Nothing is polled: the list is fetched when the tab is shown, when Refresh is pressed and after a successful save. The page is built the first time the tab is opened, and its rows are disposed whenever the list is rebuilt.

**Rows.** Each row shows the label, the description as muted text, an input that matches the setting type, the default value, and a restart badge:

| Type | Input |
| --- | --- |
| `bool` | On/off switch |
| `int`, `double` | Numeric box limited to the setting's `min` and `max` |
| `string` | Text box limited to the setting's `maxLength` |
| `enum` | Drop-down list of the setting's `options` |
| secret (`secret: true`) | No input. The row shows `set`, `not set`, `new value entered` or `will be cleared`, and the buttons Change... and Clear |

- **Modified dot and Reset to default.** A dot marks a setting whose value differs from its default. "Reset to default" puts the default into the edit; it is saved only with Save. For a secret, reset means clear.
- **applies after restart.** The badge appears for settings with `restartRequired: true`. They are saved at once but the server uses the new value only after it is restarted.
- **Secrets.** The server never returns the value of a secret (names containing `Password`, `Token`, `Secret` or `ApiKey`); it only says whether one is set. Change... opens a masked entry dialog; the new value is kept in memory until Save, is never shown again, and is not written to the activity log (the log names the changed keys only). Clear is a separate, explicit action that sends an empty string; an empty entry in the Change dialog means "no change".
- **adminApi settings** are shown disabled with the note `edit vmp-server.json and restart`. The server refuses to change them through the API.

**Search and filter.** The search box matches the label, the key and the description (every word must occur, letter case is ignored) across all sections; matches are grouped by section (at most 150 rows are shown). "Modified only" keeps the settings that differ from their default and the ones with a pending edit. Choosing a section on the left ends the search.

**Editing and saving.** Edits stay local in a set of unsaved changes; a changed row is tinted, the section list shows the number of pending edits per section, and the bottom bar shows `N unsaved changes` with Save and Discard. Editing a value back to what the server has removes the edit. Save first checks the edits against the limits of the server (type, `min`, `max`, `maxLength`, enum options); problems are shown next to the rows and nothing is sent. Otherwise one `PUT /api/v1/settings` is sent with only the changed keys. The server checks everything again and applies all changes or none:

- Rejected (`400`): the server returns every problem at once; each is shown next to its row, the section list marks sections with problems with `!`, and the edits are kept.
- `403` (an `adminApi` key), `409` (the server could not read its settings file at start, so saving is refused), `429` (too many failed authentications), `500` or no connection: the server's message is shown above the list and in the activity table; the edits are kept.
- Success: the edits are cleared, the list is read again from the server and the activity table names the saved keys.

**Restart required.** When a successful save lists `restartRequired` keys, a banner `N changes apply after a restart` appears with a "Restart now" button. The button runs the same Restart action as the Actions page, including the confirmation that names the players who are online. The banner disappears by itself when a status poll reports a different server start time (the server was restarted); until then the running server still uses the old values of those settings. Settings without the flag take effect immediately. See "Settings that need a restart" in the admin API reference for the exact rules (for example, players who are already connected keep cheat and gameplay values until they reconnect).

**Old servers.** A server without the settings endpoints answers `404 not found`; the tab then shows `This server needs the update that adds the settings endpoints.`

## Requirements

**Application**

- Windows. The project is a Windows Forms application (`net10.0-windows`, `OutputType` `WinExe`).
- To build: the .NET 10 SDK. To run a framework-dependent build: the .NET 10 Desktop Runtime. A self-contained publish needs no installed runtime.
- Windows PowerShell (`powershell.exe`, taken from the System32 `WindowsPowerShell\v1.0` folder, otherwise resolved through `PATH`) to run the control scripts.
- Remote mode only: the OpenSSH client (`ssh.exe` from the System32 `OpenSSH` folder, otherwise resolved through `PATH`).

**Server side**

- A VMP server build that contains the admin API (`Server/Admin` in this repository). The API is on by default (`adminApi.EnableAdminApi` in `Config/vmp-server.json`), listens on `127.0.0.1` only, and uses TCP port 18900 by default (`adminApi.Port` in the same file). Every request needs the bearer token stored in `Config/admin-token.txt`, which the server creates at start-up when the file is missing. The Settings page additionally needs a build with the settings endpoints (`GET` and `PUT /api/v1/settings`).
- Without the admin API the application degrades: the tray status and Start, Stop, Restart and Backup (through the control script) keep working, the other pages show a message.
- A control script:
  - host mode: `Manage-VmpServer.ps1` inside the server folder. `Scripts/Manage-VmpServer.ps1` in this repository accepts the actions the application uses (`Start`, `Stop`, `Restart`, `Status`, `Backup`) and the `-ServerRoot` parameter;
  - remote mode: `vmp-server-control.ps1` on the PC that runs VmpControl. It is not part of this project; the application only defines how it is called (see [Host mode and remote mode](#host-mode-and-remote-mode)).
- Remote mode additionally needs an SSH server on the server PC, reachable through an `ssh` host alias with non-interactive authentication (the application always passes `BatchMode=yes`, so password prompts cannot be answered). The server PC must be Windows: the application runs `powershell.exe` and `shutdown` there and expects a drive-letter path for the server folder.

## Build and publish

The project is deliberately not part of `VladMultiplayer.sln` and is built on its own. Run the commands from the repository root (the folder that contains `VmpControl` and `VmpControl.Tests`).

```
dotnet build VmpControl\VmpControl.csproj -c Release
```

The project file sets no runtime identifier and no publish mode, so `dotnet publish` needs them on the command line.

Self-contained single file:

```
dotnet publish VmpControl\VmpControl.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o <output-folder>
```

Framework-dependent (requires the .NET 10 Desktop Runtime on the target PC):

```
dotnet publish VmpControl\VmpControl.csproj -c Release -r win-x64 --self-contained false -o <output-folder>
```

The project file sets `EnableWindowsTargeting`, so restore and build also work on a non-Windows host; the application itself runs on Windows only.

**Tests**

```
dotnet test VmpControl.Tests\VmpControl.Tests.csproj
```

`VmpControl.Tests` is an MSTest project targeting plain `net10.0`. It compiles the files under `VmpControl\Core` directly (they contain no Windows Forms code). It covers settings load and save, profile validation, the logic of the Settings page (schema parsing from a real response, the dirty set and its revert, the PUT body with only the changed keys, client-side limits, secret handling, search and filtering, read-only adminApi rows, the restart banner), the host-mode control-script arguments (including a run of a stand-in script that records the `-ServerRoot` it received), command-line parsing, the SSH and script argument lists, token redaction in log text, parsing of the API's JSON, the API client against a local HTTP listener, tray state and notification logic, output parsing of the control scripts, number and duration formatting, and the SSH tunnel supervision (restart with back-off, kill on dispose; this test starts Windows PowerShell as a stand-in program, so it needs Windows).

`LiveServerTests` talks to a real server and is reported as inconclusive unless the environment variable `VMPCONTROL_LIVE_FOLDER` names the folder of a running server with the admin API. `VMPCONTROL_LIVE_PORT` selects the API port (default `18900`). That test sends a chat message, tries to kick a player that does not exist and creates a backup; a second live test reads the settings, changes the message of the day, reads it back, puts the old value back and provokes a `400` and a `403`. Point it only at a scratch server.

## Host mode and remote mode

Each profile in `settings.json` has a `Mode` of `Host` or `Remote`. In both modes the application sends its API requests to `http://127.0.0.1:<port>/api/v1/` with the header `Authorization: Bearer <token>`. No proxy is used, requests time out after a few seconds (3 seconds for status, 10 seconds for say and kick), and a backup request waits up to 15 minutes.

| | Host mode | Remote mode |
| --- | --- | --- |
| Where the application runs | On the server PC | On another PC |
| API port used locally | `ApiPort` (the server's admin API port) | `LocalTunnelPort` (local end of an SSH tunnel) |
| Token source | File `<server-folder>\Config\admin-token.txt`, read directly | The same file on the server PC, read through SSH |
| Control script | `<server-folder>\Manage-VmpServer.ps1` | `ControlScriptPath` (`vmp-server-control.ps1`) |
| Arguments passed to the control script | `-Action <action> -ServerRoot <server-folder>` | `-Action <action> -Yes` |
| Restart or abort restart of the server PC | Not available | Available |
| Reports page "Fetch and analyze..." | Not available | Available when `FetchLogsScriptPath` points to an existing file |

`<action>` is one of `Status`, `Start`, `Stop`, `Restart`, `Backup`. The control script is started as

```
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <script> -Action <action> [-Yes | -ServerRoot <server-folder>]
```

with no window and without a shell. In host mode `-Action` and `-ServerRoot <server-folder>` are passed (the profile's `ServerFolder`, without a trailing separator), so the script works on the folder of the profile and not on its own default location, for the background `Status` check as well. In remote mode `-Action` and `-Yes` are passed and no server root. Every other script parameter keeps the script's own default. Because `-ExecutionPolicy Bypass` is used, the configured scripts should be ones the user trusts. `-Yes` is added in remote mode because the remote script otherwise asks for confirmation when players are online; the application has already asked. Time limits for a script run: 45 seconds for the background status check, 60 seconds for a `Status` action, 10 minutes for `Backup`, 3 minutes for the other actions. The `Status` output is read for `Players online: <n> (<names>)` or `Players online: nobody`, for `is running`, `RUNNING` or `NOT running`, and for a JSON object with a boolean `Running` property.

### Host mode

Host mode needs `ServerFolder` and `ApiPort`. The application reads the token from `<server-folder>\Config\admin-token.txt` (opened with shared read access, so a running server does not block it) and connects to `127.0.0.1:<ApiPort>`. The Windows account that runs the application must be able to read the token file.

### Remote mode

Remote mode needs `SshAlias`, `RemoteServerRoot`, `RemoteApiPort`, `LocalTunnelPort` and `ControlScriptPath`. The application runs `ssh` hidden and without a shell, with the alias validated first. All SSH invocations use `-o BatchMode=yes -o ConnectTimeout=10`.

- **Tunnel.** One long-running child process:

  ```
  ssh -N -T -L 127.0.0.1:<LocalTunnelPort>:127.0.0.1:<RemoteApiPort> -o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=3 -o ConnectTimeout=10 <ssh-alias>
  ```

  The local end is bound to the loopback interface. The tunnel status is `starting`, `connecting`, `up` (ssh was still alive after 3 seconds) or `down - retrying in <n>s`. When ssh exits it is started again after 1, 2, 5, 10, 30 and then 60 seconds; the sequence restarts at 1 second after a run that lasted longer than 30 seconds. The child process is assigned to a Windows job object that kills it when the application ends, and it is also killed when the application disposes the session. If the local port is already in use, `ExitOnForwardFailure=yes` makes ssh exit and the tunnel keeps retrying; another `LocalTunnelPort` has to be chosen.
- **Token.** The token is read when it is first needed and then kept in memory (a failed read is retried at most every 5 seconds; after a 401 it is read again, at most once every 20 seconds) with a second ssh call, which runs `powershell.exe -NoProfile -NonInteractive -EncodedCommand <base64>` on the server PC. The encoded script only reads `<remote-server-folder>\Config\admin-token.txt` and writes it to standard output. The call has a 20 second limit. The text must look like a token (8 to 512 characters from letters, digits and `. _ ~ + / = -`).
- **Server PC restart.** The Actions page runs `ssh ... <ssh-alias> shutdown /r /t 30 /c "VMP Control restart"` after the user types `RESTART`, and `ssh ... <ssh-alias> shutdown /a` for "Abort restart".
- **Authentication.** The application never asks for or stores SSH credentials; the alias, keys and agent come from the user's own SSH configuration.

## settings.json

File name: `settings.json`. The application looks for it in two places:

1. Next to the executable (`settings.json` in the directory of `VmpControl.exe`). If the file exists there, it is used and it wins.
2. Otherwise `%LOCALAPPDATA%\VmpControl\settings.json`. The folder and file are created on the first save.

The `--settings <file>` switch replaces this lookup. Settings are written back to the path they were loaded from. The file is only written when the Settings dialog is saved, never at start-up of a valid configuration. A missing, empty or unparsable file, or one without any profile, is replaced in memory by the defaults (one remote profile and one host profile); the defaults reach the disk with the next save. The file is written through a temporary file (`settings.json.tmp`) that is moved over the original.

Reading accepts property names in any letter case, `//` and `/* */` comments, and trailing commas. `Mode` is written as the text `Remote` or `Host`. Properties that are missing keep the defaults below. The Settings dialog (tray menu, Settings...) edits profiles, notifications, the reports script and the profile to use; saving rebuilds the connection without restarting the application. At start-up the dialog opens by itself when the chosen profile is not usable; closing it without saving ends the application with exit code 1.

Example (placeholders):

```json
{
  "Profiles": [
    {
      "Name": "<profile-name>",
      "Mode": "Remote",
      "SshAlias": "<ssh-alias>",
      "RemoteServerRoot": "<remote-server-folder>",
      "RemoteApiPort": 18900,
      "LocalTunnelPort": 18901,
      "ControlScriptPath": "<path-to>\\vmp-server-control.ps1"
    },
    {
      "Name": "<host-profile-name>",
      "Mode": "Host",
      "ServerFolder": "<server-folder>",
      "ApiPort": 18900
    }
  ],
  "ActiveProfile": "<profile-name>",
  "Notifications": true,
  "FetchLogsScriptPath": ""
}
```

**Top-level settings**

| Setting | Default | Meaning |
| --- | --- | --- |
| `Profiles` | One remote profile and one host profile (see below) | List of connection profiles. |
| `ActiveProfile` | `Train PC` | Name of the profile to run. Matched without regard to letter case. When it matches nothing, the first profile is used. |
| `Notifications` | `true` | Show balloon notifications for joins, leaves, server down and server back. |
| `FetchLogsScriptPath` | empty | Path of the script that the Reports page runs with `-Analyze` in a visible PowerShell window (`-NoExit -ExecutionPolicy Bypass -File <script> -Analyze`, working directory the script's folder). Remote mode only; the button is hidden while the path is empty or the file does not exist. |

**Profile settings**

| Setting | Default | Used in | Meaning |
| --- | --- | --- | --- |
| `Name` | `Train PC` | both | Profile name, shown in the window. Must be unique (case-insensitive) and not empty. |
| `Mode` | `Remote` | both | `Remote` or `Host`. |
| `SshAlias` | `trainpc` | remote | SSH host alias. Letters, digits, `_`, `.` and `-` only, at most 64 characters, must not start with `-`. |
| `RemoteServerRoot` | `C:\VMP Server` | remote | Server folder on the server PC. Must be a drive-absolute Windows path of at most 240 characters without `"`, `&`, `|`, `<`, `>`, `^`, `%`, `` ` ``, `$` or line breaks. |
| `RemoteApiPort` | `18900` | remote | Admin API port on the server PC. |
| `LocalTunnelPort` | `18901` | remote | Local end of the SSH tunnel; the port the application connects to. |
| `ControlScriptPath` | empty | remote | Path of `vmp-server-control.ps1` on the PC that runs VmpControl. Required. |
| `ServerFolder` | empty | host | Server folder on this PC. Required. |
| `ApiPort` | `18900` | host | Admin API port. |

Every port must be between 1 and 65535. The default host profile that is created with the defaults is named `This PC (host)` and has `ServerFolder` set to `C:\VMP Server`. The Settings dialog validates all profiles in the list when it is saved, not only the selected one; the message is prefixed with the profile name.

## Command-line switches

Switch names are matched without regard to letter case; values are used as typed. There is no help switch. An unknown argument, a switch without a value or an invalid port stops the start with a message that lists the switches, and exit code 2.

| Switch | Argument | Effect |
| --- | --- | --- |
| `--mock` | none | Uses generated sample data instead of a server. No SSH tunnel or script is started, actions are simulated, and profile validation is skipped. |
| `--profile` | `<name>` | Runs the profile with this name (letter case ignored). An unknown name ends the start with exit code 2 and a message listing the profiles. Together with `--server-folder` it names the temporary profile instead. |
| `--server-folder` | `<path>` | Builds a temporary host profile for this folder, named by `--profile` or `Command line`, with the API port from `--api-port` or 18900. It is never written to the settings file. |
| `--api-port` | `<n>` (1 to 65535) | Overrides the API port of the chosen profile: `RemoteApiPort` for a remote profile, `ApiPort` for a host profile. The override is not saved. |
| `--mock-edits` | none | Only together with `--mock` and `--screenshot`: after the normal pages, renders the Settings page with several pending edits and a validation error (`settings-edits.png`), scrolled to the secret settings (`settings-secrets.png`), after a successful save with the restart banner (`settings-saved.png`) and the read-only Admin Api section (`settings-adminapi.png`). |
| `--settings` | `<file>` | Uses this settings file instead of the two default locations. |
| `--tab` | `<name>` | Page shown first: `Overview`, `Network`, `Logs`, `Backups`, `Reports`, `Actions` or `Settings` (letter case ignored). An unknown name selects Overview. |
| `--minimized` | none | Starts hidden in the tray. This is also the argument registered by "Start with Windows". |
| `--screenshot` | `<folder>` | Renders every page to `<folder>\<page>.png` (lower-case page name), plus `settings-dialog.png` (the application's own connection settings dialog) and `restart-dialog.png`, then exits. No tray icon is created and the single-instance check is skipped. It waits up to 40 seconds for the first answer from the server; exit code 2 if none arrived (the pages are still rendered). |

Exit codes: `0` normal end (also for a second instance that only asked the first one to show its window), `1` settings dialog opened at start-up and closed without saving, `2` invalid switches, an unknown profile name, an unusable profile in screenshot mode, or no answer from the server in screenshot mode.

## What the application stores

**Stored on disk or in the registry**

- `settings.json` (see above): profiles (including SSH alias and folder paths), the active profile name, the notification flag and the reports script path. The settings type has no field for a token or a password.
- When "Start with Windows" is enabled in the tray menu: the value `VmpControl` under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`, containing the quoted path of the executable followed by `--minimized`. Disabling the entry removes the value.
- With `--screenshot`: the PNG files in the given folder.

**Not stored**

- The admin token is never written to disk and is not part of `settings.json`. It is held in memory only: in the token source of the session, in the per-request `Authorization` header, and as the redaction pattern of the activity log. A 401 response makes the application forget it and read it again (at most once every 20 seconds). In remote mode the token travels from the server PC to the application as standard output of the SSH call and afterwards as an HTTP header (plain HTTP to `127.0.0.1`, carried inside the SSH connection).
- The activity log (the table on the Actions page) is kept in memory only (500 entries, 300 shown), is lost when the application ends, and every entry is passed through a filter that replaces the token and any `Bearer <value>` text with `[token]` before it is stored or shown.
- SSH passwords, keys and passphrases are never requested or stored.
- The application's own network traffic goes only to `127.0.0.1` (the admin API) and, through `ssh`, to the SSH alias of a remote profile. The control and reports scripts are separate programs started by the application.

## Troubleshooting

| Message | Meaning |
| --- | --- |
| `This server does not have the admin API yet...` / `This server needs the update that adds the admin API.` | The server answered `404 not found` to an API call, or no token file exists. Check that the server build contains the admin API and that `adminApi.EnableAdminApi` is `true` in `Config/vmp-server.json`. |
| `This server needs the update that adds the settings endpoints.` (Settings page) | The admin API answers, but `GET /api/v1/settings` returned `404 not found`: the server build is older than the settings endpoints. |
| `Fix <n> problem(s) first; nothing was sent.` | Save found edits that break the limits of a setting (type, minimum, maximum, text length, enum options). The problems are shown next to the rows; nothing was sent to the server. |
| `The server rejected <n> setting(s); nothing was changed and your edits are kept.` | `PUT /api/v1/settings` answered `400` with a list of problems, shown next to the rows. |
| `Not saved: <message>` | The server refused the save with the message shown (`403` for an `adminApi` setting, `409` when its settings file was not readable at start, `429` after too many failed authentications, `500` when the file could not be written) or could not be reached. The edits are kept. |
| `Could not load the settings: <message>` | `GET /api/v1/settings` failed (for example a timeout); press Refresh. |
| `no admin-token.txt in the server's Config folder` (host) / `no admin-token.txt on the server` (remote) | The token file is missing. The server creates it when it starts with the admin API enabled. In remote mode the same text is shown for any non-zero exit code of the remote read, so also check `RemoteServerRoot`. |
| `the token file does not look like a token` | The file content does not match the accepted token shape (see Remote mode). |
| `The server refused the admin token. Reading it again...` | HTTP 401. The server reads `admin-token.txt` when it starts; after the file changed, the server has to be restarted. |
| `The admin API is not reachable: <detail>` with `SSH tunnel: <status>` | Connection refused, timed out (`timed out`) or the tunnel is down. In remote mode check the tunnel status; if the port is in use choose another `LocalTunnelPort`. If the server was never started or crashed, the control-script fallback reports it as down. |
| `ssh could not connect: <last line of ssh error output>` | The token read via SSH ended with ssh exit code 255. Check that the alias works without any prompt, for example with `ssh -o BatchMode=yes <ssh-alias> echo ok`. |
| `ssh timed out` | The token read took longer than 20 seconds. |
| `could not start ssh.exe` / `could not start powershell.exe` | The program could not be started (the OpenSSH client or Windows PowerShell was not found). |
| `could not read the token file: <message>` | The token file exists but cannot be read (host mode). |
| `control script not found: <path>` | The script path (`ControlScriptPath`, or `Manage-VmpServer.ps1` in the server folder) does not exist. |
| `the control script timed out` | The background `Status` check exceeded 45 seconds. |
| `failed (exit <n>): <last output line>` / `timed out` | Result of a Start, Stop, Restart or Backup run through the control script. |
| `restarting the PC is only available in remote mode` | Restart and abort of the server PC exist only for remote profiles. |
| `Backup failed: <message>` | The server rejected or failed the backup; the text is the server's own error (for example when a backup is already running). |
| `could not save: <message>` | `settings.json` could not be written (activity log). |
| `The profile needs a name.` / `The ssh alias may contain only letters, digits, '_', '.' and '-' (and must not start with '-').` / `The remote server folder must be a full Windows path such as C:\Server and must not contain quotes or & \| < > ^ %.` / `Ports must be between 1 and 65535.` / `Set the path of vmp-server-control.ps1.` / `Set the server folder.` / `The API port must be between 1 and 65535.` | Profile validation, shown in the Settings dialog (prefixed with the profile name when saving). On a fresh configuration the default remote profile has no control script path, so the dialog opens at the first start. |
| `Two profiles are called "<name>".` | Profile names must be unique, ignoring letter case. |
| `VMP Control: there is no profile called '<name>'. Profiles: <list>` | `--profile` names a profile that does not exist. |
| `VMP Control: unknown argument <x>` / `<switch> needs a value` / `--api-port must be 1-65535` | Invalid command line (see Command-line switches). |
