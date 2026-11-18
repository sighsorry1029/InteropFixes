# Historical zero-config initial synchronization checks

**Retired on 2026-11-09:** this source and its recorded results target the earlier
mandatory ServerSync artifact, not the current DLL. The current optional-install
checks are documented in [OptionalNetwork](../../OptionalNetwork/README.md).

This isolated test loads the final merged InteropFixes DLL, installed BepInEx and
preserved original Valheim 1.0.21 assemblies without publicizing them. It executes
the real plugin `Awake`, embedded ServerSync serialization/receiver methods and
the module's installed Harmony completion postfix. A legacy cfg containing the
removed setting set to `false` remains unbound.

```powershell
dotnet build Tests/ServerSync/ZeroConfig/ZeroConfig.Tests.csproj -c Debug
& ../OverallPerformance/tools/MonoShutdownProbe/Invoke-Mono.ps1 `
  -Executable Tests/ServerSync/ZeroConfig/bin/Debug/net472/ZeroConfig.Tests.exe `
  -ManagedArguments @(
    'C:/Users/blizz/RiderProjects/InteropFixes/bin/Debug/netstandard2.1/InteropFixes.dll',
    '<preserved original Managed directory>',
    'C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/core')
```

Original Managed directories:

- Client: `C:/Users/blizz/.codex/references/valheim/snapshots/client-b26249040-windows-x64-20261029T094619Z-depot-restored/original/valheim_Data/Managed`
- Server: `C:/Users/blizz/.codex/references/valheim/snapshots/dedicated-server-b26266951-windows-x64-20261029T094713Z-depot-restored/original/valheim_server_Data/Managed`

Coverage includes zero registered settings, ignored legacy cfg and network keys,
retained required version checks, internal metadata with an empty settings list,
and notification after `InitialSyncDone` becomes true. Another ConfigSync instance,
an unknown RPC, a non-server peer, an old network instance, skipped original
receiver or stopping plugin cannot trigger the readiness refresh. Teardown removes
the owned completion hook.

The harness supplies managed BepInEx initialization state and engine object identity,
substitutes native logging, and observes `RefreshReadiness` without executing its
transport output. Network/peer objects and current-connection membership are fixture
state. ServerSync's actual packet serialization/parser and the real completion hook
are not replaced. The multi-endpoint coordinator suite separately exercises reports
and grants. No live socket, server authentication, Unity scene startup, rendering or
gameplay is exercised; the queued common ServerSync game-start hooks are not run.

2026-11-09: test build completed with zero warnings/errors. Both original inputs
passed **21 checks** and normal x64 Mono runtime cleanup (`NativeHostExit=0`). Logs:
`../zero-config-client-results.txt` and `../zero-config-server-results.txt`.
