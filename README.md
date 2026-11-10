# AmmoStacks (ZED ZONE)

Bigger ammo stacks for ZED ZONE.

- **Bullets and shells stack to 100.**
- **Arrows and bolts stack to 100.**
- **Ammo and arrows weigh nothing.**
- **Loot is unchanged.** Crates, shelves, corpses, vehicles, traders, rewards and dismantle drops still roll the same amounts and hand ammo out in the same stack sizes as the base game. You only see the bigger stacks once the ammo is yours (picking up, merging stacks, chests, crafting, buying).
- Works on modded ammo / arrows too (anything with item type Ammo or Arrow).

## Requirements

- ZED ZONE (Windows, Steam)
- [ZED ZONE Code Mod Loader](https://steamcommunity.com/sharedfiles/filedetails/?id=3834614258) (BepInEx 6 IL2CPP + ZedModsLoader). Run its `install.cmd` once.

## Install

- **Steam Workshop:** subscribe to AmmoStacks. The loader picks it up automatically.
- **Manually:** put `AmmoStacks.dll` in `%USERPROFILE%\AppData\LocalLow\LevenLiu\ZEDZONE\Mods\AmmoStacks\`.

Start the game through Steam. `BepInEx\AmmoStacks.log` should show `AmmoStacks 1.0.1 loaded. Loot hooks: 53/53 attached`, then one `[items]` line per ammo / arrow item with its old and new stack size and weight.

**Co-op:** everyone should have it. Stack sizes are checked on each player's own PC, so a player without the mod keeps the base-game stacks.

## Config

`BepInEx\config\girthy_mods.zedzone.ammostacks.cfg` (created after the first launch):

| Setting | Default | What it does |
|---|---|---|
| `Stacks.AmmoStackSize` | `100` | Max stack for bullets / shells. Never lowers an item below its original stack. |
| `Stacks.ArrowStackSize` | `100` | Max stack for arrows / bolts. Never lowers an item below its original stack. |
| `Weight.RemoveAmmoWeight` | `true` | Bullets / shells weigh nothing. |
| `Weight.RemoveArrowWeight` | `true` | Arrows / bolts weigh nothing. |
| `Loot.KeepVanillaLootStacks` | `true` | Loot rolls and splits ammo with the original stack sizes. Off: loot can come in bigger stacks. |
| `Debug.VerboseLog` | `false` | Extra log detail: every hook attached, loot call counts per method, every ammo loot roll. Turn on when reporting a problem. |

## Logs and troubleshooting

AmmoStacks writes its own log to `BepInEx\AmmoStacks.log` (rewritten each launch; the previous launch is kept as `AmmoStacks.prev.log`). Everything also goes to `BepInEx\LogOutput.log`. Each line has a time, a level and a category:

| Category | What it covers |
|---|---|
| `[start]` | Game / Unity / BepInEx versions, config, each loot hook (attached / not found / failed), other loaded plugins |
| `[items]` | Every ammo / arrow item changed: old and new stack size and weight |
| `[loot]` | First call of each loot hook, ammo loot rolls that came out bigger than vanilla, stuck loot scopes |
| `[watch]` | Something else (game reload, another mod) changed the ammo stack sizes or weights |
| `[status]` | Summary every 5 minutes and on exit: items tracked, hooks fired, loot rolls checked, errors / warnings |
| `[error]` | Anything that failed, with the full exception (repeats throttled) |

What to look for:

- **Stacks still at the old size:** no `[items]` lines, or an `[error]` near them. Check the `[start]` lines show the mod loaded.
- **Loot coming in bigger stacks:** a `[loot] Loot roll ... bigger than the vanilla stack` warning, or a `[start] loot method(s) not in this game version` warning after a game update.
- **Stacks resetting:** `[watch]` warnings. Usually another mod that edits items. The plugin list in `[start]` shows what else is loaded.

When reporting a problem, set `Debug.VerboseLog = true`, reproduce it, and send `BepInEx\AmmoStacks.log`.

## How loot stays vanilla

The game uses an item's stack size in two places when it makes loot:

1. `ItemManager.GetRandomDataByAttr` rolls an ammo count from the item's `ammoNumber` (80% of the time 10-30% of it, otherwise anywhere up to all of it, weighted low) and **caps it at the stack size**.
2. Containers, vehicles, vaults, rewards, dismantle drops, wall / voxel loot and so on **split what they hand out into stacks of at most the stack size**.

AmmoStacks hooks those methods (52 of them, found by disassembling the game). While one runs, ammo and arrows get their original stack size back; when it returns, the raised size goes back in. Everything else in the game sees 100.

If a game update renames one of those methods, the log says `Loot method ... not found` and loot from that one source may come in bigger stacks until the mod is updated. Nothing breaks.

## Uninstalling

Stacks bigger than the base-game size stay as they are in your save. Split them before removing the mod if you want them back to normal.

## Building

```
dotnet build src\AmmoStacks.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\ZED ZONE"
```

Add `-p:CopyToMods=true` to copy the DLL straight into your Mods folder. BepInEx must have run once so `BepInEx\interop` exists.

## Publishing to the Workshop

```
.\publish-workshop.ps1 -SteamUser <your steam login>
```

The first run creates a **private** Workshop item and saves its id to `workshop\publishedfileid.txt` (commit that file). Later runs update the same item. `-Visibility Public` makes it public, `-ChangeNote "..."` sets the change note, `-UpdateDescription` re-uploads `workshop\description.txt`. SteamCMD asks for your password and Steam Guard code itself.

## License

MIT
