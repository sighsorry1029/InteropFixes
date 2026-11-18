# Optional-install network verification

Current InteropFixes has no ServerSync dependency, merged types or version-based
admission enforcement. There are no user configuration entries. An old
`MagicPlugin.EnableNetworkCosmeticOptimization = false` value is left unbound.
Cosmetic optimization uses its own protocol-2 capability agreement; installing
receive handlers does not itself authorize replacing native FX creation.

This suite checks the final DLL, not a compile-time facade or publicized game DLL.
The older tests under `Tests/ServerSync` describe the retired mandatory design and
must not be run against the current artifact. Only their original-game metadata
contract inspector is reused here.

## Reproduce

Run from the InteropFixes project directory after the normal Debug build.

```powershell
$modDll = 'C:/Users/blizz/RiderProjects/InteropFixes/bin/Debug/netstandard2.1/InteropFixes.dll'
$clientManaged = 'C:/Users/blizz/.codex/references/valheim/snapshots/client-b26249040-windows-x64-20261029T094619Z-depot-restored/original/valheim_Data/Managed'
$serverManaged = 'C:/Users/blizz/.codex/references/valheim/snapshots/dedicated-server-b26266951-windows-x64-20261029T094713Z-depot-restored/original/valheim_server_Data/Managed'
$bepCore = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/core'

dotnet run --project Tests/OptionalNetwork/Contracts/OptionalNetwork.Contracts.csproj -c Debug -- $modDll "$clientManaged/assembly_valheim.dll" "$serverManaged/assembly_valheim.dll"
dotnet build Tests/OptionalNetwork/Runtime/OptionalNetwork.Runtime.csproj -c Debug
foreach ($managed in @($clientManaged, $serverManaged)) {
    & ../OverallPerformance/tools/MonoShutdownProbe/Invoke-Mono.ps1 `
        -Executable Tests/OptionalNetwork/Runtime/bin/Debug/net472/OptionalNetwork.Runtime.exe `
        -ManagedArguments @($modDll, $managed, $bepCore)
}
```

The contract inspector checks absence of ServerSync/config/version rejection,
soft dependencies, protocol-2 identifiers, lifecycle gates and the seven original
game hook contracts. It also checks original private RPC dictionary visibility,
public registration APIs and named Harmony arguments for both game builds.

The x64 Mono harness executes the final plugin's real `Awake`/`OnDestroy` without
the historical ServerSync initialization shim. It verifies actual Harmony patch
installation and cleanup, empty config with the legacy false value, original
`ZRpc.Register`/`Unregister` and the cached private-field access. It checks that
teardown removes only owned RPC handlers and Harmony patches, preserving another
mod's replacement handler and disconnect hook. Teardown is safe to repeat.

The harness supplies engine object identities, a temporary BepInEx config and
logger, and substitutes native Unity logging. Network objects are uninitialized
fixture instances; no socket is opened. It does not execute Unity scene startup,
rendering, live peer admission or multiplayer transport. The separate serialized
coordinator fixture covers agreement behavior; actual host/client/dedicated-server
and crossplay sessions remain a live validation step.

## Recorded result

2026-11-09, Valheim 1.0.21 client b26249040 and dedicated server b26266951:

- Test projects built with zero warnings/errors.
- **103** final-artifact/original-game metadata and IL checks passed.
- **24** runtime checks passed against each original Managed directory.
- Both x64 Mono executions completed normal runtime cleanup, `NativeHostExit=0`.

Tested DLL SHA-256:
`A076F388D486F751D1391D239A253F6D9089F9898A3F2741D8F83CA20A035E64`.

Logs: [contracts](contracts-results.txt), [client runtime](runtime-client-results.txt),
[server runtime](runtime-server-results.txt). These results do not claim live-game
or network-session validation.
