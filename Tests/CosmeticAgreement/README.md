# Cosmetic agreement readiness fixture

```powershell
dotnet run --project .\Tests\CosmeticAgreement\CosmeticAgreement.Tests.csproj -c Debug
```

This links the production `CosmeticAgreement` state into a pure .NET executable. It does not load Unity, Harmony, ServerSync, transport sockets or game DLLs, and does not deploy or publish a mod.

The server caches the intersection of its local structural features and every registered connection's known, ready capabilities. Any unknown connection or incomplete scene/probe readiness yields zero permission. There is no user policy toggle: readiness and capability loss still revoke permission. InteropFixes installation is optional: a peer without a coordinator remains unknown and vetoes optimization; an installed peer with zero verified MagicPlugin features is valid and also vetoes it. The model never rejects a game connection. Invalid or old reports cannot replace newer state. Mutations advance the server revision, while duplicate membership/local notifications do not erase capabilities or churn revisions.

Connection tokens must be fresh positive identities allocated by the runtime adapter for each `ZRpc` connection; they are not player UIDs and must not be reused after reconnect. A positive server session nonce scopes reports to one world/server session. The adapter remains responsible for sender validation, mapping the current connection, assigning readiness only after the peer is admitted, rejecting stale grant packets on the client, and resetting state on disconnect/world change.

This policy is **not an atomic network agreement or admission barrier**. Existing clients may use their previous permission until a reduction arrives. The tests therefore do not claim to eliminate asynchronous late-join races. Receive/replay capability for already committed carrier markers and in-flight events must remain independent from permission to suppress new effects.

2026-11-09: **71 checks passed**, including mixed supported, missing-installation
and zero-capability members, partial departures, late capability discovery and an
unsupported server. See `optional-install-results.txt`; no game or transport was
executed. Earlier `automatic-results.txt` predates the optional-installation
coverage.
