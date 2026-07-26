# InteropFixes

InteropFixes is a small, configuration-free compatibility plugin for Valheim.
It keeps runtime fixes that do not belong to one gameplay mod in a single
standalone DLL.

## Included fixes

When `blacks7ar.MagicPlugin` is present, InteropFixes detects it by plugin GUID
and enables each compatible fix independently:

- Replaces the `Attack.FireProjectileBurst` prefix that repeatedly registers
  configuration event handlers with a null-safe, per-attack implementation.
- Replaces the followed-summon teleport postfix with a guarded implementation
  that skips destroyed or incomplete characters.
- Removes destroyed entries from Valheim's Character registry before
  `MagicPlugin.Plugin.Update` reads it.

The following creature-removal protections are available whenever
InteropFixes is installed:

- Tracks Characters removed through `ZNetScene.Destroy`.
- Safely tears down StatusEffect start effects with missing or invalid
  `ZNetView` components.
- Repairs Character registry and EnemyHud bookkeeping when
  `Character.OnDestroy` is incomplete or throws.

MagicPlugin is an optional integration, not a hard assembly dependency.
InteropFixes does not lock support to one MagicPlugin version: it installs a
capability only when the expected type, method, patch owner, signature, and
configuration fields are present.

## Installation

Install BepInEx for Valheim and place `InteropFixes.dll` in
`BepInEx/plugins`. Install it on the dedicated server and on clients that use
the affected mod combination.

There are no configuration files.
