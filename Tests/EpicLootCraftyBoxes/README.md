# EpicLoot / AzuCraftyBoxes integration verification

The implementation is in `Modules/EpicLootCraftyBoxes`. The standalone
`EpicLootCraftyBoxesBridge` project is preserved as the migration source. The
combined plugin has no assembly reference to either optional mod.

The host activates this module after both plugins initialize, on clients only,
and skips it if the standalone bridge or its legacy hooks are installed. Each
integration has a separate Harmony owner. This bridge has no ServerSync, own
configuration, RPC, ownership request, inventory rollback or inventory cloning.
It reuses CraftyBoxes' nearby-container, access and range policies.

## Preserved behavior

- Guard enchant, augment, disenchant and Rune operations before consuming
  materials. Delayed augment completion repeats the guard.
- Track the exact source item/inventory/container/ZDO identities and owner
  revision. Only ordinary, locally owned, closed containers are supported.
- Track authoritative stored item bytes and load/save sequence numbers. A
  reload, external item update, changed ownership, replaced inventory or missing
  item prevents a stale final save.
- Save the final live item after successful actions, including late durability
  changes. Keep original exceptions. Never promise refunds after consumption.
- Allow intentional Rune destruction only when the normal provider removal has
  already saved; never recreate or match/reinsert an equivalent item.
- Cache reflected accessors once. Observe/record explicitly as well as through
  postfixes because pre-existing delegates may bypass patched entry points.

## Contracts and failure boundary

Startup validates exact declaring types, signatures, field types/staticness and
relevant original IL call landmarks for UI actions, provider enumeration,
removal, wrapper persistence and game persistence. Versions, whole-file hashes,
MVIDs and metadata tokens are not activation allowlists. These structural checks
do not prove arbitrary changed code is semantically equivalent.

At each UI operation boundary the module checks its complete prefix/finalizer/
postfix ownership and relevant competing prefixes/transpilers, without reparsing
IL. Unknown competing mutations cause the storage bridge to fail closed; ordinary
player-inventory actions remain usable. Startup failures remove only this
module's hooks and leave base game/mod behavior in place, with a warning to move
equipment into player inventory. Stop makes hooks inert before removing them,
then releases tracked state/accessors even if removal reports a failure.

An independently installed late prefix may already have executed before this
last-priority guard sees it. This boundary check cannot undo prior side effects
or guarantee compatibility with arbitrary late patches. Runtime DLL replacement
is unsupported. World disconnect resets only session tracking; full teardown
also releases reflected bindings and patch ownership.

## Checks run on 2026-11-07

`BridgeSmoke` links all three production module source files with narrow test
doubles. **113 assertions passed**: source identity, persistence, access/range,
ownership/reload changes, provider exclusions, cached delegates, Rune deletion,
exception preservation, optional dependencies, live coverage failure, foreign
mutators, own-owner teardown and failed-removal inertness. This is a state-machine
simulation, not Unity or actual Harmony dispatch.

`OriginalContracts` performs read-only Mono.Cecil inspection. **97 metadata/IL
checks passed** against original Valheim 1.0.21 and the reviewed optional mods:

| Assembly | Identification SHA-256 |
| --- | --- |
| assembly_valheim.dll, original client 1.0.21 | `9F959C0396EC4240D47FCBE5D0D062C428CC627244506DB5EA567F9D77B2B769` |
| EpicLoot 0.14.13 | `802790E0CBD8A6F951627619A488B84E6A406E8770E2687B2FFF8B63D2ADF9EA` |
| AzuCraftyBoxes 1.8.28 | `381883FE4625D1DD119BB425A2F3C7522457FBC08AE1DB60159F8256DE940D6E` |

The reviewed pair's actual Harmony declarations were also checked. EpicLoot's
`Container_Load_Patch`, CraftyBoxes' `ContainerLoadPatch` and
`ContainerSaveInvalidateCountsPatch` are postfixes, so the foreign-mutation guard
permits them. No reviewed action/provider/wrapper target has a pair-owned prefix
or transpiler. Manual patch sites were inspected too: Auga redirects its own API;
AnimationSpeedManager installs postfix/finalizer hooks; ItemInfo patches target
Inventory, ItemData, ItemDrop, InventoryGui and Player rather than the guarded
Container/action/provider methods. This is static declaration/site inspection,
not proof of arbitrary third-party patches registered later at runtime.

`RuntimeContracts` loads the built combined plugin and original DLLs under the
existing Unity Mono x64 host. Actual cached UI delegate compilation, reflected
action binding and relevant provider/game IL checks passed; the final assembly
has no EpicLoot/AzuCraftyBoxes AssemblyRef. Normal Mono cleanup returned exit 0.
No Unity object was constructed and no external plugin entry point, game action,
Harmony dispatch or network session was executed.

Run the first two projects with `dotnet run --project <project> -c Debug`.
Build `RuntimeContracts` in Debug, then run it through
`OverallPerformance/tools/MonoShutdownProbe/Invoke-Mono.ps1`, passing four managed
arguments: final `InteropFixes.dll`, reviewed `EpicLoot.dll`, reviewed
`AzuCraftyBoxes.dll`, installed Valheim root. These test projects do not create
Release packages or deploy DLLs.

Still requires live client/host checks with closed nearby containers, ownership
transfer, both augment stages, Rune extraction modes and disconnect/reconnect.
No live game/server compatibility claim is made by these checks.
