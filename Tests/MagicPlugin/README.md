# MagicPlugin migration and replacement checks

2026-11-07: the latest run passed **37 assertions**, zero build warnings/errors,
and normal Unity Mono cleanup with exit 0. These are isolated managed checks;
the game, MagicPlugin startup, its actual spell effects, and multiplayer were not run.

The harness loads the final InteropFixes and Blasted DLLs. Its resolver uses the
unchanged Valheim 1.0.21 original client assemblies, not publicized references.
It checks that Magic modules moved completely, general Character lifecycle fixes
remain in Blasted, no hard MagicPlugin/Blasted assembly reference was added,
the old carrier marker remains intact, and the internal installer requires explicit
receiver preparation. The coordinator separately gates new cosmetic suppression.

Actual Harmony patches on managed fixtures exercise production replacement,
preserved order, original restoration, repeated restoration, rollback after an
invalid replacement, and rejection of an additional Magic-owned target patch.
Contract checks accept changed assembly identity/version/MVID, an unrelated method
change, and a no-op insertion. They reject relevant body/helper changes and a
Harmony hook on a reachable helper. Test fixture DLLs are written only under the
temporary directory; original game/mod DLLs are not modified.

```powershell
dotnet build Tests/MagicPlugin/MagicPlugin.Tests.csproj -c Debug
& 'C:/Users/blizz/RiderProjects/OverallPerformance/tools/MonoShutdownProbe/Invoke-Mono.ps1' `
  -Executable 'C:/Users/blizz/RiderProjects/InteropFixes/Tests/MagicPlugin/bin/Debug/net48/MagicPlugin.Tests.exe' `
  -ManagedArguments @(
    'C:/Users/blizz/RiderProjects/InteropFixes/bin/Debug/netstandard2.1/InteropFixes.dll',
    'C:/Users/blizz/RiderProjects/BlastedSwimmingTarredBugFix/bin/Debug/netstandard2.1/BlastedSwimmingTarredBugFix.dll',
    'C:/Users/blizz/.codex/references/valheim/snapshots/client-b26249040-windows-x64-20261029T094619Z-depot-restored/original/valheim_Data/Managed',
    'C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/core',
    'C:/Users/blizz/AppData/Roaming/com.kesomannen.gale/cache/blacks7ar-MagicPlugin/2.2.2/BepInEx/plugins/blacks7ar-MagicPlugin/MagicPlugin.dll'
  )
```

The fifth argument is optional. Supplying it also checks that both replacement
contracts match that MagicPlugin DLL, adding two assertions. Without it, no
MagicPlugin assembly is loaded. All four inspected versions (2.1.9, 2.2.0,
2.2.1 and 2.2.2) produced the same two contract values and passed their checks.
See [the contract provenance and limitations](../../refer/magic-contract-20261107.md).
