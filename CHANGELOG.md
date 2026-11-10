# Changelog

## 1.0.1
- Own log file: `BepInEx\AmmoStacks.log` (previous launch kept as `AmmoStacks.prev.log`), with categories `[start] [items] [loot] [watch] [status] [error]`.
- Logs game / Unity / BepInEx versions, the config, every loot hook (attached, missing, failed) and the other loaded plugins.
- Checks every ammo loot roll and warns if one ever comes out bigger than the vanilla stack.
- Watchdog: warns when something else (a game reload or another mod) changes the ammo stack sizes or weights, then re-applies.
- Logs the first call of each loot hook, a status summary every 5 minutes, and a summary on exit.
- Every hook is wrapped so an error is logged (with inner exception + stack trace, throttled) instead of breaking the game's loot code.
- `Debug.VerboseLog` adds per-hook call counts and every ammo loot roll.

## 1.0.0
- Bullets / shells and arrows / bolts stack to 100 (configurable) and weigh nothing.
- Loot, traders, rewards and dismantle drops keep the base-game ammo amounts and stack sizes (52 loot methods hooked).
- Also applies to modded ammo and arrows, including ones registered after the game starts.
