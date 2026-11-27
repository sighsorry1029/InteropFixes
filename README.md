# InteropFixes

InteropFixes applies compatibility fixes for the mods you have installed. Install it through your mod manager on the client, host or dedicated server where you want the fixes to run. Setup is automatic.

## Compatibility

- **VNEI** uses the item under your cursor for recipe lookup, even when two items have the same display name.
- **EpicLoot and AzuCraftyBoxes** save changes to gear in nearby chests after enchanting, augmenting, disenchanting or using runes. This works with closed, standard chests your client controls.
- **Path of Valheiman** checks for BetterArchery, ExtraSlots and SeneaLUI without triggering VNEI warnings about missing optional mods.
- **MagicPlugin** fixes teleporting with summons and errors from creatures that have already despawned. Firing a magic weapon no longer registers duplicate listeners for projectile settings. In multiplayer, the game draws supported spell effects locally when the server and all connected players support the optimization.

[GitHub repository](https://github.com/sighsorry1029/InteropFixes)
