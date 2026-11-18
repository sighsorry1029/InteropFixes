# MagicPlugin replacement contracts, 2026-11-07

The moved projectile and summon-teleport fixes replace two third-party Harmony
methods. Their names and parameter types alone are insufficient compatibility
checks. `Modules/MagicPlugin/MagicPatchContract.cs` now examines the exact replaced
method and directly/transitively referenced methods in the same assembly before
removing the external patch. This includes captured lambda bodies, constructors,
and the anonymous-type accessors used by the teleport LINQ pipeline.

The inspected projectile prefix still subscribes configuration handlers during
every attack. Its replacement applies current configuration once without those
accumulating subscriptions, with null/view guards. The inspected teleport postfix
still enumerates Characters through a component lookup before its null guard and
moves followed BMP summons without checking the original teleport result. The
replacement validates references and runs only after a successful teleport.

## Inputs

| MagicPlugin | Original DLL location | SHA-256 (identification only) |
| --- | --- | --- |
| 2.1.9 | `C:/Users/blizz/RiderProjects/InventorySlots/reference/MagicPlugin.dll` | `8C8C87BFC8D2CFDA3C4D655A25D481BC1AFDC47A2B47697491D79A95ABAC8837` |
| 2.2.0 | `C:/Users/blizz/AppData/Roaming/com.kesomannen.gale/cache/blacks7ar-MagicPlugin/2.2.0/BepInEx/plugins/blacks7ar-MagicPlugin/MagicPlugin.dll` | `1413E513DDEDEE09AC7980BB00F0E96C7AA9FCF9ABE471AE820011854DFB2F99` |
| 2.2.1 | `C:/Users/blizz/AppData/Roaming/com.kesomannen.gale/cache/blacks7ar-MagicPlugin/2.2.1/BepInEx/plugins/blacks7ar-MagicPlugin/MagicPlugin.dll` | `ACD6100977B8714679D2267DDD733367F8950916732E4097C01DD10D35DED87E` |
| 2.2.2 | `C:/Users/blizz/AppData/Roaming/com.kesomannen.gale/cache/blacks7ar-MagicPlugin/2.2.2/BepInEx/plugins/blacks7ar-MagicPlugin/MagicPlugin.dll` | `6504C2FF9CEAAE7B317820E41EF84D8C28D33FEDBF32B7D0F4FDDFD680BDA795` |

Existing decompilations were reused:

- `C:/Users/blizz/RiderProjects/InventorySlots/reference/decompiled/MagicPlugin/MagicPlugin.Patches/AttackPatch.cs`
- `C:/Users/blizz/RiderProjects/InventorySlots/reference/decompiled/MagicPlugin/MagicPlugin.Patches/PlayerPatch.cs`
- `C:/Users/blizz/Documents/Codex/2026-09-18/d/work/magicplugin-2.2.2/MagicPlugin.Patches/AttackPatch.cs`
- `C:/Users/blizz/Documents/Codex/2026-09-18/d/work/magicplugin-2.2.2/MagicPlugin.Patches/PlayerPatch.cs`

Runtime fingerprints were computed from original loaded method bodies using the
production normalizer, independently of those decompiled C# files. All four
versions produced these same values:

| Replaced method and its helper graph | Normalized contract SHA-256 |
| --- | --- |
| `MagicPlugin.Patches.AttackPatch.FireProjectileBurst_Prefix` | `17BAA2C31BB6E9DD4DA2F3B85E3726AAB53FD4AC8CD7D9F2835E4EBC91A97538` |
| `MagicPlugin.Patches.PlayerPatch.TeleportTo_Postfix` | `21E126B35A09509ADAEE71DB5440E8B20306EEC82EAB236D25ED38564C200AAF` |

## Scope and limits

- The guard retains operations, fields, method signatures, constants, control-flow
  targets, local types, initialized-local semantics, exception boundaries and
  connected helper bodies. Whole DLL hashes, assembly versions, MVIDs, metadata
  tokens and numeric branch offsets are not permission conditions.
- No-ops and short opcode encodings are normalized. Generated closure/anonymous
  type numbers and lambda cache method numbers are normalized so unrelated
  compiler numbering changes do not automatically reject the integration.
- At installation, exactly one expected Magic-owned patch of the expected kind
  must exist on the target. Additional Magic-owned patches reject that replacement.
  Active Harmony hooks on the replaced method or its reachable helpers also reject
  replacement. Other mods' target patches are retained unchanged.
- A mismatch leaves the corresponding original Magic patch active. It does not
  disable saving, joining, other Interop modules, or the independently validated
  Magic capability. Existing original member/config access checks still apply.
- This is a bounded structural guard, not a proof of arbitrary program equivalence.
  Semantically equivalent compiler refactoring can require renewed review. Config
  values remain live inputs, and unrelated plugin initialization/resources are not
  hashed. The test does not prove gameplay results on future game/mod versions.
- Validation happens at startup. Runtime assembly replacement or adding/removing
  Harmony patches to this contract after installation is not supported; restart
  after changing the mod set. There is no per-frame IL/reflection scan.
- The registry cleanup remains an additive guard on Magic's Update, not a replaced
  method. Cosmetic support uses its separate prefab-graph probes and retains the
  persisted `BlastedSwimmingTarredBugFix_MagicCosmeticCarrier` key. It is now an
  explicit default-off option because InteropFixes does not globally require every
  peer to install the bundle. Enable it only with matching support/options on the
  server and every affected client; capability negotiation was not added.

Validation: original Valheim 1.0.21 client DLLs, Harmony 2.9 and Unity Mono isolated
host; contract acceptance across four original MagicPlugin DLLs; mutation tests
for unrelated changes, relevant changes and foreign helper hooks; actual managed
replacement/rollback/restoration checks. No Unity gameplay or multiplayer run was
performed. Commands and the latest assertion count are in
[`Tests/MagicPlugin/README.md`](../Tests/MagicPlugin/README.md).
