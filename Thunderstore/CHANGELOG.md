# Changelog

## 1.0.3

- Fixed followed-summon teleport compatibility on standard Valheim assemblies by accessing `Character.m_nview` and `Tameable.m_monsterAI` through validated Harmony field references.
- Kept MagicPlugin's original teleport patch installed when the required Valheim field accessors cannot be created.

## 1.0.2

- Fixed MagicPlugin projectile compatibility on standard Valheim assemblies by obtaining `Attack.m_character` and `Attack.m_weapon` through Harmony private-field injection.
- Restored the configured magic projectile velocity, accuracy, area-of-effect, and heritage damage adjustments that were skipped after the access failure.

## 1.0.1

- Fixed private-field access failures in the Character destruction and StatusEffect teardown compatibility patches.
- Prevented failed lifecycle prefixes from interrupting vanilla cleanup and leaving stale Character or Player references that could trigger repeated MonsterAI and portal errors.
- Added runtime field capability checks so lifecycle compatibility is skipped safely if a future Valheim update changes the required fields.

## 1.0.0

- Added standalone MagicPlugin projectile, summon teleport, and Character
  registry compatibility fixes.
- Added tracked creature-removal protection for StatusEffect teardown and
  Character destruction bookkeeping.
- Added GUID-based MagicPlugin discovery with independent structural checks
  for each capability.
- Added safe Harmony rollback and restoration for runtime reloads.
