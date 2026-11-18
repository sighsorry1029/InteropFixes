# Integration verification

2026-11-10: the independent [PoV / VNEI optional lookup suite](PathOfValheimanLookup/README.md)
checks the four original PoV lookup sites, unchanged gameplay instructions,
optional-mod absence, legacy VNEI coexistence and scoped Harmony cleanup.
The [callback dispatch probe](PathOfValheimanLookup/DispatchProbe/README.md)
also checks callbacks patched before and after their outer Harmony wrapper is created.
These are isolated checks, not a live Valheim session.

2026-11-09: local compatibility patches no longer require InteropFixes on the
opposite endpoint. ServerSync references, merging, mandatory-version checks and
the initial-sync completion hook have been removed. There are no user settings.
Network cosmetic suppression still needs the server's v2 capability agreement,
native peer admission, current scene/feature readiness and a valid session grant.
Absent servers never grant permission; silent or unsupported clients keep the
server agreement disabled. Grants are sent only after a valid capability report.

Current policy and validation are recorded in
[automatic cosmetics](../docs/automatic-cosmetics.md). Current final-artifact and
original-DLL startup/teardown checks are in [OptionalNetwork](OptionalNetwork/README.md).
The counts and hashes below
describe earlier mandatory-install stages, including their former ON/OFF option
and embedded ServerSync. They are not current optional-install validation results.

All production builds in this change are Debug builds. No Release package,
version increment, uploader operation or publication was performed.
Original Valheim 1.0.21 assemblies are used without publicizing them.

## Earlier ServerSync and cosmetic follow-up checks

The following checks were rerun after the final Debug merge/deployment for the
new required server/client policy and coordinated cosmetics. Counts describe
their stated inputs, not a live game session.

| Suite | Scope | Completed result |
| --- | --- | --- |
| Host | Final DLL type loading without optional mods, soft dependencies, legacy GUID/actual hook detection, real Harmony owner isolation and failed-install rollback | 38 assertions; Unity Mono x64 exit 0 |
| [VNEI](Vnei/README.md) | Linked production identity logic, actual Harmony lifecycle, original VNEI IL, original game access contracts and final DLL dependency checks | 542 assertions |
| [EpicLoot/CraftyBoxes](EpicLootCraftyBoxes/README.md) | Linked state/persistence/rollback simulation | 113 assertions |
| EpicLoot/CraftyBoxes OriginalContracts | Actual game/mod metadata, related call flow and reviewed Harmony declarations | 97 checks |
| EpicLoot/CraftyBoxes RuntimeContracts | Final DLL cached UI adapter compilation and actual original-DLL call contracts | Unity Mono x64 exit 0 |
| [MagicPlugin](MagicPlugin/README.md) | Final DLL migration boundary, original replacement restoration and scoped method/helper contracts, with original MagicPlugin 2.2.2 | 37 assertions; Unity Mono x64 exit 0 |
| [CosmeticAgreement](CosmeticAgreement/README.md) | Pure session/revision/readiness/mask policy and report/readiness transitions | 64 assertions |
| [CosmeticCoordinator](CosmeticCoordinator/README.md) | Linked coordinator adapter, connection/scene lifecycle, reordered reports and cleanup fixtures | 50 assertions |
| [MagicCosmetics](MagicCosmetics/README.md) | Linked production packet/history logic, original game DLLs, actual routed-RPC constructor hook and registration ownership, committed receive/emission boundaries | 61 assertions; Unity Mono x64 exit 0 |
| [ServerSync MergedContracts](ServerSync/README.md) | Final DLL/pinned input merge, both-side required-version policy, default ON/sync/lock, receiver installation, coordinator hooks and original client/dedicated-server contracts | 121 static assertions |

The cosmetic fixture controls the coordinator boundary; the coordinator and pure
agreement suites cover that component separately. Passing all three does not
create a globally atomic network transition. See the late-join limitation below.

## Earlier migration baseline

These results were recorded before the ServerSync/capability follow-up. They are
retained as a regression baseline; they do not claim the current final DLL was
already rerun through every suite.

| Suite | Scope | Result |
| --- | --- | --- |
| Blasted RuntimeOptimizations | Final merged Blasted DLL and original game IL/access paths after Magic removal | 513 assertions; Unity Mono x64 exit 0 |

The earlier pinned `valheim-1.0.7-r1` ServerSync input and its checks are retained
as history. See [provenance](../Libs/ServerSync.provenance.md) and
[artifact/ServerSync verification history](ServerSync/README.md). Current builds
do not reference or merge ServerSync. Absence of optional
MagicPlugin/VNEI/EpicLoot/AzuCraftyBoxes assembly references remains a separate
requirement.

These are managed fixtures, actual assembly inspection and isolated Harmony
dispatch checks. They do not start Unity/Valheim, execute third-party plugin
entry points, measure frame rate or prove live multiplayer behavior.
The Host fixture initializes BepInEx paths inside a fresh temporary sandbox;
it does not load or modify the user's BepInEx configuration.
Earlier Host and MagicPlugin checks used `Fixtures/ServerSyncHost.cs` to provide the managed
part of BepInEx's `ThreadingHelper` queue without constructing a Unity GameObject.
The ServerSync game-start hooks queued by initialization are **not executed** in
these checks. Passing them is not verification of the real BepInEx/Unity startup
sequence or live ServerSync RPC installation.

## Host reproduction

From the InteropFixes project root:

```powershell
dotnet build InteropFixes.sln -c Debug -p:DeployToGame=true
dotnet build Tests/Host/Host.Tests.csproj -c Debug
& '../OverallPerformance/tools/MonoShutdownProbe/Invoke-Mono.ps1' -Executable 'Tests/Host/bin/Debug/net472/Host.Tests.exe'
```

The host defaults to the installed original client Managed directory. An
alternate original Managed directory can be supplied with `-ManagedArguments`.
It checks the built `bin/Debug/netstandard2.1/InteropFixes.dll`.
Each module README describes its additional inputs and commands.
The x64 runner performs normal runtime cleanup and propagates nonzero exits.

## Boundaries to verify in a real session

- Client-only, server-only, all-installed and mixed installed/uninstalled sessions,
  with none, one or several of the optional target mods; Steam and PlayFab/crossplay joins.
- Silent/unsupported/v1 peers keep new cosmetic suppression disabled without the
  new DLL rejecting game admission. Old binaries may still reject missing or
  mismatched versions through their own mandatory ServerSync code.
- VNEI mouse and gamepad lookup after moving items or switching containers.
- Enchant/augment/disenchant/Rune actions, reopen/rejoin persistence, and changed
  ownership/access while the UI is open. Use backed-up test equipment.
- MagicPlugin projectile/summon behavior and registry teardown. For network
  cosmetics, verify automatic activation after peer admission and the v2 agreement, prefab
  mismatch and skipped-module fallback. Retired configuration values are ignored.
- Permission loss during an effect, repeated scene creation, source destruction
  before event delivery, gamepad-specific effects, duplicate events and late joins.
- Clean disconnect/reconnect and removal of old standalone DLLs. Arbitrary
  runtime plugin/DLL loading is not supported.

No running game process was stopped or restarted during this migration.

## Asynchronous agreement limitation

Receiver hooks are prepared independently of current new-work grants.
Already marked launch/Nova carriers and explicitly committed hit/destruction
events keep their receive paths. Native `RPC_OnHit` is not used as an optimization
signal, and an old Arctic creation-time marker cannot authorize death replay.

A new connection causes a downgrade, but existing clients receive it
asynchronously. An effect can have been committed under the preceding agreement
while the late joiner is still being checked. No global atomic admission/ZDO
delivery barrier is implemented, and arbitrary mismatched prefab sets in this
window, including an uninstalled late joiner, are not guaranteed to render identically. No actual multiplayer or Unity
rendering/audio validation has been completed by these checks.

## Earlier Debug deployment — 2026-11-07

The follow-up InteropFixes Debug build, ServerSync merge and deployment completed
with zero warnings/errors. The final output and installed plugin SHA-256 matched:

| DLL | SHA-256 |
| --- | --- |
| InteropFixes.dll (merged) | `9A31B75E5D41111A3895CE53396A197AEF7669548990A459CA65141B9A3B7E9C` |

Blasted was unchanged by this network-policy follow-up. Its earlier verified
migration DLL hash was
`CEF6FDABBE35D9AD1242DFA5E51AAA6479291662A9C85B4B7F3D57ABA8908CDB`.

Installation target: `C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/plugins`.
The two old standalone DLLs were moved outside that folder to
`refer/installed-standalone-20261107`, and their hashes verified before/after
the move. The source projects and Gale profiles were preserved.
DLL changes take effect after restarting the game.
