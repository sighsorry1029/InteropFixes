# VNEI item identity regression fixture

This executable links the production files from `Modules/Vnei`. Unity, game objects and the plugin registry are simulated; Harmony itself is the installed BepInEx Harmony. It neither deploys a mod nor starts Valheim.

```powershell
dotnet build .\Tests\Vnei\Vnei.Tests.csproj -c Debug
& .\Tests\Vnei\bin\Debug\net481\Vnei.Tests.exe '<VNEI.dll>' '<original game Managed directory>' ['<final InteropFixes.dll>']
```

The optional final DLL argument checks that the built bundle has no static VNEI/InventorySlots assembly dependency or private-access bypass attribute. Run against original game DLLs, not publicized copies.

Validated on 2026-11-07: 540 assertions with VNEI 0.17.6.0 (SHA-256 `A496F106B325414EFF2F19D1C48AFB522B8F10359F0571CECB85754D53012EBD`) and original Valheim 1.0.21 client build 26249040. These identifiers record the inputs; runtime admission does not use version, hash or MVID equality.

Coverage:

- Missing VNEI, dedicated/headless execution and standalone `sighsorry.VneiItemLookupFix` registration skip this module.
- Actual Harmony installation replaces all three lookup calls and restores their original VNEI item stack type. A foreign transpiler that changes the lookup responsibility makes installation roll back without removing the foreign owner.
- Identity wins over display-name collisions. Prefab/shared-name/native fallback order, reused slots, changed inventories, inactive UI, mouse/camera/gamepad/touch selection and weak binding reset are exercised.
- Successful teardown removes all three hooks. An intercepted, silently unsuccessful Harmony unpatch retains a callable native lookup adapter while disabling identity and refusing reinitialization; later cleanup completes.
- The original VNEI `Plugin.Update` IL must contain the topic/text/local flow, rich-tag fallback and recipe consumer. The rewrite preserves unrelated instructions, branch labels and exception boundaries. Changed lookup input, missing calls/fields or a missing consumer are rejected atomically; unrelated additions are allowed.
- Original game metadata confirms the private tooltip patch target/current-tooltip field and the public inventory accessors/tooltip overload.

The IL guard is a narrow structural compatibility check, not a proof of arbitrary third-party behavior. Existing or later Harmony streams are rechecked when Harmony regenerates the target. Runtime replacement of the VNEI DLL is unsupported. Actual Unity UI/gamepad sessions, mod load order in BepInEx and end-user gameplay remain separate validation.
