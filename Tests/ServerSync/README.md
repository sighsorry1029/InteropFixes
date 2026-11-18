# Historical ServerSync integration verification

**Retired on 2026-11-09:** the current optional-install implementation contains no
ServerSync. The commands, sources and results below describe the earlier mandatory
handshake artifact and must not be run against the current DLL. Current checks are
in [OptionalNetwork](../OptionalNetwork/README.md). The reusable original-game
contract inspector is still linked by that suite; no ServerSync behavior is reused.

InteropFixes vendors the fixed common `valheim-1.0.7-r1` ServerSync artifact.
`Libs/ServerSync.provenance.md`, the original manifest and MIT-0 license identify
the exact input. ILRepack internalizes that input into the final plugin before
Debug deployment or existing Release packaging. No standalone ServerSync.dll is
deployed, and these changes do not invoke Release packaging.

The existing mod version remains 1.0.3. ConfigSync requires InteropFixes on both
ends and uses that version for current/minimum compatibility. There are no user
configuration entries. The removed `MagicPlugin.EnableNetworkCosmeticOptimization`
key is ignored even if an old cfg or an older peer's message contains `false`.
Automatic eligibility still requires the current scene, completed initial
synchronization, local feature support and an authoritative server grant.

ServerSync sends internal version/admin metadata even with zero registered settings.
A scoped completion postfix on the embedded `RPC_FromServerConfigSync` refreshes
coordinator readiness only after the original receiver succeeds for this module
and the current server connection. This avoids depending on a setting-change event
that no longer exists. The pinned DLL and its serialized payload format are unchanged.

`original-1.0.21-verification.json` summarizes the common static verifier run
against the installed original client and preserved original dedicated-server
1.0.21 assemblies on 2026-11-07. It passed with zero failures. Public API entries
match the 73-entry prior baseline. Readonly-field writes, dynamic deserialization
and reflection sites remain explicitly unexecuted limitations; this is not live
Unity/network validation.

After building the final plugin, run:

```powershell
dotnet run --project Tests/ServerSync/MergedContracts/MergedContracts.csproj -c Debug -- bin/Debug/netstandard2.1/InteropFixes.dll Libs/ServerSync.dll
```

This checks the pinned input checksum, final merge/internalization, absence of a
ServerSync AssemblyRef, required version and lock policy, client readiness and
authority gates, removal of all configuration/event plumbing, and receiver
preparation independent of new-work eligibility. It checks both required-version
rejection paths and normalized core/version-handshake IL preservation. The
coordinator's seven original game lifecycle targets plus its embedded initial-sync
hook, Harmony-injected argument names, private RPC dictionary and public peer/
registration APIs are checked. Pass original client and server assembly paths as
the third and fourth arguments to inspect both builds.

2026-11-09: the updated final-artifact check passed **125 assertions** against both
original assemblies. See `automatic-merged-results.txt`. The [zero-config runtime
check](ZeroConfig/README.md) also passed **21 checks per original client/server
input**, with normal x64 Mono cleanup and `NativeHostExit=0`.

Original coordinator metadata can be checked without a finished plugin build:

```powershell
dotnet run --project Tests/ServerSync/MergedContracts/MergedContracts.csproj -c Debug -- --original-only <client-assembly_valheim.dll> <server-assembly_valheim.dll>
```

Earlier baseline: this standalone original-DLL check passed **74 assertions** against 1.0.21 client
b26249040 and dedicated server b26266951. The expanded final-artifact check passed
**121 assertions** against both originals and InteropFixes.dll SHA-256
`9A31B75E5D41111A3895CE53396A197AEF7669548990A459CA65141B9A3B7E9C`.
These checks execute neither plugin
initialization nor actual game connections. Host, remote client, dedicated server and crossplay joins,
including missing/wrong versions and reconnects, still need live validation.
