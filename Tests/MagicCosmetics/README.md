# Cosmetic event and receiver checks

2026-11-07: **61 assertions passed**, build warnings/errors 0, Unity Mono normal
cleanup/exit 0. The project links the current production cosmetic source and uses
unchanged original Valheim 1.0.21 assemblies. Its coordinator is a controlled
boundary fixture; the actual agreement transport/state machine has separate tests.

Checked behavior:

- Actual `ZRoutedRpc(bool)` Harmony hook and original private RPC-map access;
  receiver registration, duplicate attachment, session replacement, preservation
  of unrelated/replaced handlers, and capability fallback on a name collision.
- Three explicit event types round-trip through real `ZPackage` serialization:
  Thunder hit, Arctic hit, Arctic iceblock destruction. Payloads are 52 bytes.
- Stale/unknown sessions, malformed lengths, composite/unknown feature types,
  empty source IDs, invalid coordinates and rotations are rejected.
- Source/feature duplicate suppression, bounded history, session and scene reset.
- Receiver readiness remains available when new suppression lacks agreement.
- Local-effect cleanup restores only its own ZNetView initialization context.
- Compiled code keeps committed notifications/replay independent of the current
  agreement, preserves the old carrier key, and no longer treats native `RPC_OnHit`
  or an Arctic creation-time marker as proof that network effects were omitted.

```powershell
dotnet build Tests/MagicCosmetics/MagicCosmetics.Tests.csproj -c Debug
& 'C:/Users/blizz/RiderProjects/OverallPerformance/tools/MonoShutdownProbe/Invoke-Mono.ps1' `
  -Executable 'C:/Users/blizz/RiderProjects/InteropFixes/Tests/MagicCosmetics/bin/Debug/net48/MagicCosmetics.Tests.exe' `
  -ManagedArguments @('C:/Users/blizz/RiderProjects/InteropFixes/bin/Debug/netstandard2.1/InteropFixes.dll')
```

The optional DLL argument checks the final product's compiled receiver/emission
boundaries with Cecil. Without it, those checks inspect the linked-source test
assembly. The executing packet/history/registration tests always use linked
production source and original game types; no copied or publicized game fixtures.

The new event is emitted only after the owner actually creates the localized
effects. Native `RPC_OnHit` routing/body is left intact. Current agreement changes do
not cancel a committed event in the same session. Events are validated against the
known session/participants, local supported feature, finite payload, nearby active
area, and existing source ownership/prefab when the source ZDO still exists. A
global event can arrive after the source object is removed, unlike a ZDO-target RPC.
The receiver stores the most recent 4096 source/feature decisions per session;
this is bounded recent-event deduplication, not permanent replay storage.

Launch/Nova carrier IDs retain their existing marker key and values. Their receive
paths remain ready after new-work permission is lost. New Arctic iceblocks are not pre-marked for
death replay, and old Arctic marker values are no longer authoritative: gamepad
recipient paths can keep original network effects, so the old marker alone could
otherwise cause duplicate effects. Old unmarked native hit RPCs likewise carry
no optimization decision and are no longer used as a cosmetic replay signal.

Not tested: Unity prefab creation/rendering/audio, actual host/dedicated-server
sessions, Steam/PlayFab networking, mid-flight disconnects/ownership changes or
late joins. Cached peer agreement is not a globally atomic admission barrier;
incompatible prefab sets or a peer joining during a previously committed effect
remain a live-integration limitation. Network send failure after local creation
does not manufacture replacement network effects or replay the operation twice.
