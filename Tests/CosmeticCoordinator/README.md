# Cosmetic capability coordinator fixture

```powershell
dotnet run --project .\Tests\CosmeticCoordinator\CosmeticCoordinator.Tests.csproj -c Debug
```

The fixture links the production coordinator and agreement state. Each endpoint is loaded in its own collectible `AssemblyLoadContext`, giving server and clients independent production static state. The fake `ZRpc` dispatches registered callbacks using a stable-hash handler table, and packets cross endpoints as serialized byte arrays. This exercises actual protocol v2 parsing, identity checks, state transitions and handler cleanup. ServerSync is neither loaded nor stubbed; negotiation depends on native admission, scene readiness and verified local capabilities.

Unity objects, game lifecycle, Harmony installation and physical transport are simulated. Lifecycle hooks are explicitly invoked around the fake native operations in their intended order. Passing these tests does not establish actual Harmony ordering or Steam/PlayFab game compatibility. A missing integration is modeled by an admitted peer that never sends a cosmetic report; the fake unmodded RPC ignores unregistered messages. Native disconnect calls are counted so the tests can detect rejection initiated by this coordinator, but do not model other mods' connection policies.

Coverage includes:

- Initial hello/report/grant exchange, grant-before-hello and pre-admission grant rejection, reordered deferred reports and local/remote feature intersection.
- Missing server installation, missing client installation, supported/unsupported mixtures, and valid zero-capability MagicPlugin absence on either side. Missing peers retain their native connection, veto optimization, and receive no grants before demonstrating report support.
- Legacy RPC name and incompatible protocol header rejection, valid protocol recovery, invalid-mask report rejection and duplicate report suppression.
- Unknown late joiners, removal, report/grant reordering, zero-support optional modules, session and connection nonce rejection.
- Grant validation, participant membership and bounded departure grace, immediate local capability/lifecycle downgrade.
- Native admission after hello, scene readiness after admission/capability discovery, and capability discovery after scene completion. Scene teardown immediately revokes local permission; a new scene cannot inherit capabilities, and late callbacks after shutdown cannot re-enable the module.
- Exact owned RPC cleanup while preserving a foreign replacement, reconnect with the same player UID, stale grants before/after the new hello, and world-session reset.
- Malformed packets, failure latching, client unavailability reports, disconnect cleanup errors and broadcast continuation past one failing socket.

One test intentionally confirms the asynchronous late-join limitation: the server invalidates its permission immediately, while an existing client keeps its previous grant until the zero update arrives. This fixture does not call that interval an atomic agreement or claim zero-loss effect transitions. Rendering already committed cosmetic events remains a separate responsibility.

2026-11-09: **90 checks passed** for optional installation and protocol v2. See
`optional-install-results.txt`. Earlier `automatic-results.txt` records the
superseded mandatory ServerSync design. Actual Unity/Harmony/network execution
remains outside this fixture.
