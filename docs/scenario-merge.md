# Shared scenario merge (Kerbal Konstructs)

Most scenario modules are stored on the server as one blob per module and replaced whenever a client uploads its
copy ("last writer wins"). That is wrong for state that several players change independently, because a client that
joined earlier uploads a stale copy and silently undoes what another player did. The server therefore **merges**
uploads for the modules listed in `ScenarioNodeMerge.IsMergedModule`. Today that is `KerbalKonstructsSettings`,
which holds Kerbal Konstructs launch site and facility open state, mission logs, player-placed objects and the
player's KK preferences.

## How it works

1. When a client requests its scenarios, the server remembers the exact text it sent for each merged module
   (the client's *baseline*, `ClientStructure.ScenarioMergeBaselines`).
2. When that client uploads its copy, the server compares the upload with the baseline. Anything the client did not
   change is left exactly as the server has it now; only the client's own changes are applied on top.
3. The upload becomes the client's new baseline.

Code: `Server/System/Scenario/ScenarioNodeMerge.cs` (the merge), `ScenarioClientMerge.cs` (baselines and locking),
wired in `Server/System/ScenarioSystem.cs`. Tests: `ServerTest/ScenarioMergeTests.cs`. An end-to-end check over the
real protocol against a local server is `Tools/VmpScenarioClient` (see its `run-scenario.ps1`).

## Rules

| Situation | Result |
|---|---|
| Two players change different entries (different launch sites, a hangar and a site...) | Both changes are kept. |
| A stale client uploads without touching an entry | The entry keeps the other player's value. |
| Both change the same value | The later upload wins. |
| `MissionCount` (launches per site) | Merged as the server's value plus the client's change, so two launches from one site count as two. |
| Facilities and launch sites (defined by the mod's files) | Never removed because a client lacks them. A client with a different mod set does not delete them for everyone. |
| Player-placed objects (`KKBuildings`, `KKGroups`, `KKMapDecals`) | Added and edited per object (matched by `UUID` for buildings, body plus group name for groups, full content for decals). **Never removed by a client**: a client that cannot load a model drops the object from its own save, and that must not delete it for everybody. The cost is that deleting a placed object in game does not stick; it returns when the player rejoins. |
| A client with no baseline (it joined before the server had any copy) | Only entries the server lacks are added; the server's values are kept. |

## Limits

- Changes are not pushed live. A player sees another player's change after rejoining; Kerbal Konstructs only applies
  the server copy once per visit to the main menu.
- Editing a map decal adds a copy instead of changing it (decals have no unique value).
- Everything is last writer wins per value, including the player preferences stored in the same module
  (`lastLaunchSiteUsed`, map filters).
- Other scenario modules still use the whole-blob overwrite. To merge another module, add it to `MergedModules` and,
  if its children repeat under one name, to `IdentityKeys`.
