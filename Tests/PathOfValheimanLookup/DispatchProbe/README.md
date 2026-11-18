# Patching an already registered Harmony postfix

This managed fixture models the original PoV `ItemAscension.UpgradeListClean.Postfix` lookup/cache shape. It keeps the original callback's non-lookup work and a foreign postfix intact while applying and removing a transpiler directly to the private callback. The outer patched method is never regenerated after callback replacement or removal.

Each mode must run in a fresh process so an earlier detour cannot affect a later mode's JIT/inlining state. The callback deliberately has no `NoInlining` attribute, matching the original PoV callback. Only the fixture game-method stand-in has that attribute so the test caller cannot bypass Harmony.

```powershell
dotnet build .\Tests\PathOfValheimanLookup\DispatchProbe\DispatchProbe.csproj -c Debug
# Invoke each command separately: Invoke-Mono.ps1 exits its host PowerShell process.
powershell -NoProfile -File ..\OverallPerformance\tools\MonoShutdownProbe\Invoke-Mono.ps1 -Executable .\Tests\PathOfValheimanLookup\DispatchProbe\bin\Debug\net481\DispatchProbe.exe -ManagedArguments early
powershell -NoProfile -File ..\OverallPerformance\tools\MonoShutdownProbe\Invoke-Mono.ps1 -Executable .\Tests\PathOfValheimanLookup\DispatchProbe\bin\Debug\net481\DispatchProbe.exe -ManagedArguments cold
powershell -NoProfile -File ..\OverallPerformance\tools\MonoShutdownProbe\Invoke-Mono.ps1 -Executable .\Tests\PathOfValheimanLookup\DispatchProbe\bin\Debug\net481\DispatchProbe.exe -ManagedArguments warm
```

Validated 2026-11-10 with the installed HarmonyX 2.9.0 and unchanged Unity 6000.0.46f1 Mono x64: early registration, already registered/cold wrapper, and already registered/warm wrapper all dispatch the replacement and restore the original callback after teardown without rebuilding the outer wrapper. All 40 assertions passed across the three processes, with normal runtime cleanup and exit 0.

This proves the managed Harmony dispatch mechanism, not gameplay correctness. It does not load PoV, native Unity objects, or Valheim, and it does not modify installed DLLs. Production initialization should still happen in InteropFixes `Awake`, after PoV's dependency-ordered `Awake` but before initial player/UI callbacks, to avoid misses being cached before installation. Actual game load order and startup/gameplay remain separate validation.
