# Sharing Kerbal Konstructs edits (statics, launch sites, facilities)

Kerbal Konstructs (KK) saves edits made in its in-game editors (Facility Editor, Launch Site Editor, statics editor)
to the player's own cfg files in `GameData`. Only statics placed through KK's career editor (save-game statics) end up
in KK's scenario (`KerbalKonstructsSettings`, node `KKBuildings`), which VMP already syncs. So when one player turns a
pack static into a launch site or changes its facility, the server only learns the site's open state, never the site
itself, and nobody else sees it.

VMP now shares those edits inside the same scenario, as one extra child node that KK itself ignores.

Code: `LmpClient/Systems/KerbalKonstructsShare/` (capture and apply), `LmpClient/Harmony/KerbalKonstructsSharedStaticsHarmony.cs`
(hooks), `LmpCommon/KerbalKonstructs/KkSharedStatics.cs` (format, identity, rules), `Server/System/Scenario/ScenarioNodeMerge.cs`
(merge). Tests: `ServerTest/ScenarioMergeTests.cs` (`SharedStatic_*`), `LmpCommonTest/KkSharedStaticsTests.cs`, and the
end-to-end run `Tools/VmpScenarioClient/run-scenario.ps1 -Script kkshare`. Background on the merge: `docs/scenario-merge.md`.

## Design

| Step | Where | What happens |
|---|---|---|
| Select | Postfix on `KerbalKonstructs.SelectInstance` (any selection: the statics editor's list selects with `isEditing = false`, and the Facility and Launch Site editors work on `KerbalKonstructs.selectedInstance`) | VMP snapshots what KK's cfg writer would write for the static now. The writer closes the launch site and facilities as a side effect (`LaunchSiteParser.WriteConfig` calls `SetClosed`, `KKFacility.WriteConfig` sets `isOpen = false`); VMP puts their two state fields back. |
| Capture | Postfix on `StaticInstance.SaveConfig` and on `KerbalKonstructs.SaveObjects` | Only for statics selected this session and only while connected. KK has just written the cfg file; VMP reads back the `Instances` node KK wrote for that UUID (exactly what KK writes, including `Group`, `LaunchSite`, `Facility`, `GrassColor2`) and shares it if it differs from the snapshot, is new this session, or was already shared by this player. `SaveObjects` ("Save all", and KK's own old-format conversion at the first Space Center visit) calls `SaveConfig` for every cfg file; those calls are ignored (flag set in a prefix, cleared in a finalizer) and only the selected statics are captured. Save-game statics are skipped (KK's `KKBuildings` carries them). |
| Upload | Postfix on `KerbalKonstructsSettings.OnSave` | Appends `VmpSharedStatics` with the player's own entries plus the entries received at join, unchanged. The normal scenario sync (every 30 s, only when the module changed) sends it; a captured edit additionally triggers an upload about a second later of the KK module only (at most one every 5 s, from ScenarioSystem's Update routine, not from KK's GUI code). Only one scenario send runs at a time (`ScenarioSendCoordinator`); each send builds its own lists (`ScenarioSendPlanner`) and marks a module as sent only after the message went to the sender. A flush that finds a send running stays requested and is retried at the next tick. |
| Merge | Server, `ScenarioNodeMerge` | The upload is sanitized first (`SanitizeShared`), then `VmpSharedStatics` is an additive container whose `SharedStatic` children are matched by `VmpKey` and replaced as a whole when the uploading client changed one since its baseline. Never removed. The merge runs on a copy: if it throws, the stored module and the baseline both stay unchanged. |
| Apply | Prefix on `KerbalKonstructsSettings.OnLoad` | KK loads its scenario once per visit to the main menu (`KerbalKonstructs.gameTime == -1`). Before KK's body runs, VMP applies every received entry with KK's own loading code, so KK's `ResetFacilitiesOpenState` and `CareerState.Load` that follow give the shared statics their saved state. |
| Guard | Prefix on `CareerState.Load` | Called only from KK's gated `OnLoad`, after `CareerObjects.LoadBuildings`, so every static exists: pack statics, shared statics and the save-game buildings KK just made. Removes saved facility states KK would crash on (below). |

Applying an entry (`KkSharedStaticsSync.ApplyOne`) follows `KerbalKonstructs.LoadInstances` / `CareerObjects.LoadBuilding`
step by step: new `StaticInstance`, `ConfigParser.ParseInstanceConfig`, `Orientate`, `LaunchSiteManager.AttachLaunchSite`,
`KerbalKonstructs.AttachFacilities`, then `Activate` or `Deactivate` to match its group. A static with the same identity
that already exists (the pack copy) is removed with `StaticInstance.Destroy` first: KK has no update-in-place path, and
remove-and-spawn is what KK itself does with save-game statics in the same `OnLoad`. The replaced static keeps its cfg
path (KK rewrites a cfg file with all of its statics; one missing would be dropped from that file). A static new to
this install gets no cfg path, so KK never writes it into this player's GameData. VMP never writes cfg files itself.

The local static is found, in order, as the one spawned from this entry earlier in this game process, the one with the
entry's UUID (also for `loc:` entries: after a restart the sharer has that UUID in its file), then for `loc:` entries
by locator. Before anything local is removed, the entry is checked (`KkSharedStaticPlanner.Decide`): a `uuid:` key must
match the UUID inside, the local static must be the same model, the model and body must exist, the group must already
exist on the receiver, the static must not be a save-game static or sit in a save-game group (KK deletes those
groups with their children on every load), and it must not be open in a KK editor. If spawning still fails after the
local copy was removed, the local copy is spawned again from its cfg node.

**Facility states that crash KK.** KK's `CareerState.LoadFacilitiesUUID` reads `myFacilities[Index]` of the static with
that UUID with no bounds check. A player who adds a facility to a pack static saves a state for index 0; on a client
whose copy of that static has no facility, KK throws and its `OnLoad` stops half-way (launch site states and ground
stations not loaded). The same happens when a save-game building's facility is set to None: its old state stays on
the server. This already happened with the plain scenario merge, before shared statics. In a prefix on
`CareerState.Load` (the first moment every static exists, including save-game buildings KK creates inside `OnLoad`),
VMP removes states whose index is beyond the local static's facilities or not a number (`KkFacilityStateGuard`). Both
orders work: with the shared static applied first the state loads; without it (entry refused, old server, edit not
shared yet, facility removed) it is skipped with one log line per join.

The server does not prune such states. It cannot tell "this client's static lost its facility" from "this client
lacks the static or has an older copy" (the reason the Facilities container never shrinks), and with the guard a stale
state costs one log line, not a broken load.

**Groups are never created from shared data.** A group spawned from a shared description would have no cfg path, so
KK's `GroupCenter.Save` would write it into the receiver's GameData as `KK_GroupCenter_<group>.cfg` on the next
"Save all"; and spawned before KK's `CareerGroups.LoadGroups`, it makes KK rename its own save-game group of that name
(`GroupCenter.Spawn` appends `_0`), so save-game statics attach to the wrong group. A shared static whose group the
receiver lacks is skipped with one log line. Entries written by the first build with a `VmpGroupCenter` node are
read, and that node ignored.

Launch sites are registered by KK under their name, so the selector, the editor's site list and VMP's launch pad
coordination (keyed by site name) all see a shared site with no extra code.

### Alternatives considered

- **A new message type.** Needs new server storage and persistence, a protocol change both ways, and old clients
  cannot parse an unknown message subtype. Its one advantage, pushing changes live, is still possible later through the
  existing `ScenarioMessageType.Proto` path (see Phase 2).
- **Writing the shared cfg into the receiver's GameData.** Needs a game restart, edits the mod pack folder (the
  launcher's file checks and pack updates would fight it), and leaves stale files behind.
- **Turning edited statics into save-game statics.** KK would then load the pack copy and a save-game copy with the
  same UUID; `StaticDatabase.AddStatic` gives the second one a new random UUID on every load, so the server's
  `KKBuildings` would collect a new copy every session. Rejected.

## Data format

```
KerbalKonstructsSettings            (KK's scenario node, unchanged)
    ...                             (KK's own values and nodes)
    VmpSharedStatics
    {
        SharedStatic
        {
            VmpFormat = 1
            VmpKey = uuid:<UUID>    (or loc:<model>|<body>|<group>|<x>|<y>|<z>)
            ModelName = <KK model name>
            UUID = ...              (then every value KK wrote for the instance)
            CelestialBody = ...
            Group = ...
            RelativePosition = ...
            LaunchSite { ... }      (as KK writes it)
            Facility { ... }        (as KK writes it, one per facility)
        }
    }
```

**Identity.** Pack statics carry their UUID in the pack's cfg file, so every client with the same pack has the same
UUID: the key is `uuid:<UUID>`. When a static has no UUID in its file, KK makes a random one at every start, which
differs per client; then the key is the model, body, group and position relative to the group (rounded to 0.1 m),
taken before the player moved it. A static created in this session (new or duplicated) gets a `uuid:` key: nobody
else has it yet, and the UUID is in the player's file from then on. When the player saves a static for which an entry
with the same UUID already exists (own or received), that entry's key is kept, so a `loc:` static does not turn into a
second `uuid:` entry after KK wrote its UUID into the file and the game restarted.

**Entries with a higher `VmpFormat`** are skipped by older clients, so the format can grow.

## Limits

- One entry at most 16 KB, at most 256 entries, at most 512 KB in total (`KkSharedStaticsFormat.MaxEntryBytes`,
  `MaxEntries`, `MaxContainerBytes`). Size is counted the same way on both sides (`MeasureContent`: trimmed non-empty
  lines plus one byte each, without the entry's own name and braces), so an entry the client accepts at the limit is
  accepted by the server. Enforced by the client (own entries and the upload) and by the server (every upload and the
  first stored copy). Server refusals and merge errors are logged once per connection and text, at most 100 per
  connection, then one line says the rest is suppressed until the player reconnects.
- The server keeps only the first `VmpSharedStatics` node of an upload, drops plain values and other child nodes in
  it, and refuses entries without exactly one valid `VmpKey` and a second entry with the same key in one upload.
- The server adds a new entry only while it stays within 256 entries and 512 KB; an edit that would cross 512 KB is refused.
- Uploads ride on the existing scenario sync: every 30 s when the KK module changed, plus one about a second after a
  captured edit (at most every 5 s). Capture happens only when the player saves a selected static in a KK editor.

## Compatibility

- **Old client, new server.** KK rebuilds its node on save, so an old client never sends `VmpSharedStatics`. The
  container is additive, so its absence deletes nothing. Old clients receive the node and KK ignores it.
- **New client, old server.** The old server stores the whole blob of the last uploader. Because every new client
  sends back what it received plus its own entries, shared statics survive as long as the last uploader had them.
  An old client uploading drops them on an old server; deploy the server first.
- **Disconnect or another server.** Received and own entries, the selected statics and the once-per-problem log gate
  are cleared when VMP disconnects (`ScenarioSystem` disabled), so nothing of one server is uploaded to the next. What
  was applied stays recorded for the game process, so a rejoin only respawns statics whose shared entry changed.
  Because an edit is uploaded about a second after the save, only an edit saved right before a disconnect can be lost.
  Then the server still has the older version, which replaces the static on the next join; saving it again would
  write that older version. Redo the change in the editor and save; the log line `Scenario save carries ...` after
  `Shared your edit ...` shows it went out.
- **KK missing or a different KK version.** Every KK type and member VMP uses (55) is resolved by name at startup
  (`KkReflection.TryCreate`). If one is missing nothing is patched and one log line says which. Verified against
  KerbalKonstructs.dll file version 1.12.3.0 (assembly 2.0.0.0): all members resolve and all six Harmony patches
  apply (checked outside the game by loading the real DLLs).
- **Every hook catches everything.** A problem with one entry is logged once per connection
  (`OncePerKeyGate`) and never reaches KK or the scenario load.

## Log lines

Client (`KSP.log`, prefix `[VMP]: [KKShare]`):

- `Sharing Kerbal Konstructs edits with the server is on.` at startup (or `... is off: ...` with the reason).
- `Shared your edit of static <model> (<key>), launch site <name>; ...` after a save in a KK editor.
- `Scenario save carries <n> shared static(s), <m> edited by you this session.` when the count changes.
- `Received <n> shared static(s) from the server.` and `Applied shared static <key> (<model>, launch site <name>): replaced the local copy.`
  (or `spawned, it was not in your install.`), then `Shared statics: <a> applied, <u> already up to date, <s> skipped (of <n>).`
- `Shared static <key> (<model>) not applied: <reason>.` once per problem.

Server: `[KKShare] KerbalKonstructsSettings from <player>: added|updated shared static <key>` (Info) and
`... refused shared static <key>: <reason>` (Warning).

## Known gaps (Phase 1)

- **Changes are not live.** Other players see an edit after they rejoin (KK loads its scenario once per visit to the
  main menu). Players already online keep the old static until then.
- **Deleting a shared static does not stick.** The entry stays on the server, and a static deleted in KK comes back on
  the next join for everybody, including the player who deleted it. Removing an entry needs a server-side edit of
  `Universe/Scenarios/KerbalKonstructsSettings.txt` while the server is stopped.
- **Edits saved before this feature** (or while not connected) are only in that player's cfg files. Open the static in
  the KK editor once more and save it to share it.
- **A shared edit wins over later pack updates** of the same static: if a pack update moves a static somebody edited,
  the shared copy still applies.
- **Map decals, groups and model settings** are not shared. A static in a group that exists only on the sharer's
  machine (made with KK's group editor) is skipped by everybody else; put shared statics into groups of the pack.
- A pack static without a UUID in its file that was moved before it was ever shared is matched by its moved position,
  so other clients get a second copy next to their original.
- Statics copied with the group editor's copy function are not selected one by one and are not shared.
- **KK itself may write received edits into the receiver's pack files.** VMP never writes a cfg file, but a replaced
  pack static keeps its pack cfg path, and KK rewrites a cfg file with all of its statics whenever the receiver saves
  another static in that file, presses "Save all", or KK runs its old-format conversion at the first Space Center
  visit. The file then holds the shared version (the launcher's file check may report it). Avoiding that would mean
  giving the replaced static no path, and KK would then drop the pack static from that file on its next rewrite,
  which is worse. Statics new to the receiver get no path and are never written, and no groups are created.
- Shared statics applied in one game process stay until the game restarts, also when joining a different server.

## In-game test (two players)

1. Deploy the new server and give both players the new client. Player A and player B join.
2. Player A: open KK's editor (KK toolbar button, Ctrl+L for the editor mode selector, Ctrl+K in flight), select a pack static that
   has a default launch pad transform (a launch pad model), open **Make Launchsite**, give it a new name, save; then
   open the **Facility Editor** on the same static, change the facility type, **Save Facility**.
   A's `KSP.log` shows `[KKShare] Shared your edit of static ...; uploading it now.` (twice) and within seconds
   `Scenario save carries 1 shared static(s), 1 edited by you this session.`
   The server log shows `[KKShare] ... added shared static uuid:...` and later `updated shared static ...`.
3. Player B (online the whole time) sees nothing yet: expected (no live push). B returns to the main menu and rejoins.
   B's `KSP.log` shows `Received 1 shared static(s) from the server.` and
   `Applied shared static uuid:... (<model>, launch site <name>): replaced the local copy.`
4. B opens the launch site selector in the VAB/SPH: the new site is listed, with A's open/closed state; launching
   from it spawns the vessel on the pad. With pad coordination on, a vessel of A on that pad blocks it for B.
5. A rejoins: A's log shows `Shared statics: 0 applied, 1 already up to date, ...` (A already has the edit).
6. A presses "Save" (all) in the statics editor without selecting anything new: no new `Shared your edit` line.
7. For the existing `KSC_PAD_2` made before this feature: its owner opens it in the Launch Site Editor and saves once,
   then continue from step 3. If its facility state is already on the server, B's log may first show
   `Saved <type> state for static <uuid> skipped: your copy of that static has 0 facilit(ies)` instead of a KK exception.

## Phase 2 (live push): recommendation

Not built. KK can spawn and remove statics at runtime (its editors do), but applying a respawn under a player's feet is
risky: a vessel standing on the replaced pad in flight, a KK editor or the launch site selector open on it, and the
Making History selector caching sites in the editor. A safe version:

1. After a merge that added or updated shared entries, the server sends the changed entries to the other connected
   clients as a `ScenarioProto` message with a module name of its own (for example `VmpKKSharedStatics`), so old
   clients queue it and then skip it as an unknown module.
2. The new client handles that module itself (not through the scenario queue, which is only drained at game start),
   stores the entries, and applies them only in the Space Center or Tracking Station scenes with no KK editor open;
   otherwise on the next such scene.
3. It counts the entry as received, so the next upload echoes it unchanged.

Effort: about a day plus a two-player test.
