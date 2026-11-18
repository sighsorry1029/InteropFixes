# PoV / VNEI optional-type lookup verification

The fixture links the production sources in `Modules/PathOfValheimanLookup`.
It uses the original PoV and game DLLs for metadata, actual Harmony instruction
streams, and installation/removal of the four transpilers. The BepInEx plugin
registry and InteropFixes logger are simulated. No game callback or third-party
plugin `Awake`/`Start` is executed.

Validated on 2026-11-10: **99 checks passed**, build with zero warnings/errors,
Unity 6000.0.46f1 Mono x64 native host exit 0 and normal runtime cleanup.
HarmonyX is the installed 2.9.0 runtime. The separate
[dispatch probe](DispatchProbe/README.md) passed 40 assertions across early,
cold and warm registration orders; it models invocation of an already installed
Harmony postfix and does not run the original game callback.

## Inputs

| Input | Identification |
| --- | --- |
| PoV | Plugin 6.0.7; assembly 1.0.0.0; MVID `80aaa21a-83e5-4ca8-94de-2768683d33f4`; SHA-256 `D6F7D6AE1D4772650E8657AF030022ED2873FE430B48FADF5A57C076FA4069E1` |
| VNEI | 0.17.6.0; MVID `59ba9f17-d780-4122-a1b6-51ed07aba84d`; SHA-256 `A496F106B325414EFF2F19D1C48AFB522B8F10359F0571CECB85754D53012EBD` |
| Original client `assembly_valheim.dll` | Valheim 1.0.21; SHA-256 `9F959C0396EC4240D47FCBE5D0D062C428CC627244506DB5EA567F9D77B2B769` |
| Final InteropFixes Debug DLL | SHA-256 `5794067B8986F6C15088736ACF19B0F1B91BC2A4A5EC28379A8AC82226570701` |

These identify the inspected inputs. Production acceptance does not require
matching versions, hashes or MVIDs. The test generates an in-memory copy of PoV
with a changed assembly name, version and MVID and confirms that the same four
behavioral contracts remain accepted. Installed and archived originals are not
changed or publicized.

## Coverage

- A generated assembly with an unavailable base-class dependency demonstrably
  throws during `GetTypes()`. Exact lookup still finds its healthy type and
  returns null for an absent type without enumerating unrelated types.
- Exact names, private/inherited fields, silent optional absence, null-safe
  SeneaL field lookup, and delayed type registration without a negative cache.
- Original `BetterArcheryQuiver.Tick` / `Sync`, `TradeMechanics.get_ExtraSlots`
  and `ItemAscension+UpgradeListClean.Postfix` IL. Only the five type-lookup
  operands and the one SeneaL field-lookup operand change. Other instructions,
  gameplay operands, branch labels and exception boundaries are preserved.
- Related lookup, once-only cache and native recipe-cleanup changes are rejected
  atomically. Unrelated no-ops, including a cache branch destination attached to
  a no-op, are accepted without changing the source stream.
- Actual module installation against the four original PoV targets, missing
  VNEI/PoV, unexpected plugin identity, independent standalone VNEI lookup fix,
  idempotence, partial installation rollback and preservation of foreign hooks.
- A later foreign transform deactivates only its changed target's replacement;
  the other lookup paths remain active. Teardown removes only this module's
  four transpilers and preserves foreign hooks.
- Final DLL has no mandatory VNEI, PoV, EpicLoot, BetterArchery, ExtraSlots or
  SeneaLUI assembly reference and no private-access bypass attribute.

The module test uses an uninitialized original PoV plugin instance solely for
assembly identity. It initializes BepInEx paths to a fresh temporary sandbox
before allocating it, so PoV's static path checks cannot migrate user settings.
The isolated Mono host supplies only Unity's object-ID offset internal call for
that identity object; it does not create a Unity scene or call any game method.

## Reproduction

Run from the project root, using the original installed game references:

```powershell
dotnet build Tests/PathOfValheimanLookup/PathOfValheimanLookup.Tests.csproj -c Debug

$plugins = 'C:/Users/blizz/AppData/Roaming/com.kesomannen.gale/valheim/profiles/fdasafdsadfs/BepInEx/plugins'
$arguments = @(
    "$plugins/treextr-PathOfValheiman/PathOfValheiman/PathOfValheiman.dll",
    "$plugins/MSchmoecker-VNEI/VNEI.dll",
    'C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed',
    'C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/core',
    'C:/Users/blizz/RiderProjects/InteropFixes/bin/Debug/netstandard2.1/InteropFixes.dll'
)
& '../OverallPerformance/tools/MonoShutdownProbe/Invoke-Mono.ps1' `
    -Executable 'Tests/PathOfValheimanLookup/bin/Debug/net481/PathOfValheimanLookup.Tests.exe' `
    -ManagedArguments $arguments
```

The final DLL argument is optional. `Invoke-Mono.ps1` exits its PowerShell host;
use a separate shell for other commands after the test.

## Limits

No actual Valheim startup, gameplay, dedicated server, multiplayer or UI session
was run. The original PoV equipment and recipe callbacks are inspected and
patched, but not invoked by this fixture. The dispatch probe covers the managed
Harmony invocation mechanism with a small stand-in. Live mod-loading order,
the six reported VNEI warnings disappearing, recipe UI behavior and equipment
interactions still require a game session.

Exact `Assembly.GetType` lookup does not eagerly validate every dependency of a
returned type. Unity Mono can return a `RuntimeType` whose base class is resolved
later. Lookup exceptions that actually arise at `GetType` are diagnosed once;
later method/field compatibility retains PoV's existing handling. Arbitrary
runtime DLL replacement is unsupported, and PoV retains its once-only caches.
