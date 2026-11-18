# InteropFixes

Compatibility fixes for Valheim mods.

## Features

- **MagicPlugin:** Prevents duplicate projectile configuration callbacks and handles summon teleports and stale creature references.
- **MagicPlugin visual effects:** Reduces unnecessary network objects when the server and all connected players support the cosmetic effects.
- **VNEI:** Looks up the actual hovered item instead of relying only on its displayed name.
- **Path of Valheiman + VNEI:** Avoids unnecessary type-loading warnings while PoV checks for optional BetterArchery, ExtraSlots and SeneaLUI integrations. Existing equipment and recipe behavior is preserved.
- **EpicLoot + AzuCraftyBoxes:** Saves container inventory changes after supported enchanting, augmenting, disenchanting and rune actions.

Each integration runs independently and activates only when the relevant mods are supported.

General Valheim runtime fixes are maintained in **Blasted**.
