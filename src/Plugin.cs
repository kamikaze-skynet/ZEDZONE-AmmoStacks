using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace AmmoStacks
{
    // Ammo and arrows stack to 100 (configurable) and weigh nothing.
    //
    // Loot stays vanilla. The game uses ItemAttr.stackNumber two ways when it makes loot:
    //   * ItemManager.GetRandomDataByAttr rolls an ammo count from ammoNumber and caps it at stackNumber;
    //   * crates, shelves, vehicles, vaults, rewards, dismantle drops... split what they hand out into
    //     stacks of at most stackNumber.
    // So while any of those loot methods runs, ammo/arrow items get their original stackNumber back,
    // and the raised one is put back as soon as it returns. Stacking in your inventory, chests,
    // picking up, crafting, buying: all use the raised size.
    //
    // Logging: BepInEx\AmmoStacks.log (see Log.cs).
    [BepInPlugin(Guid, "AmmoStacks (100-round ammo & arrow stacks, no weight)", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "girthy_mods.zedzone.ammostacks";
        public const string Version = "1.0.1";

        internal static ManualLogSource L;
        internal static ConfigEntry<int> AmmoStack, ArrowStack;
        internal static ConfigEntry<bool> AmmoWeightless, ArrowWeightless, VanillaLoot, VerboseLog;
        internal static int HooksWanted, HooksOk, HooksPlain, HooksMissing, HooksFailed;

        public override void Load()
        {
            L = Log;
            AmmoStack = Config.Bind("Stacks", "AmmoStackSize", 100, "Max stack size for bullets / shells (ItemType Ammo). Never lowers an item below its original stack size.");
            ArrowStack = Config.Bind("Stacks", "ArrowStackSize", 100, "Max stack size for arrows / bolts (ItemType Arrow). Never lowers an item below its original stack size.");
            AmmoWeightless = Config.Bind("Weight", "RemoveAmmoWeight", true, "Bullets / shells weigh nothing.");
            ArrowWeightless = Config.Bind("Weight", "RemoveArrowWeight", true, "Arrows / bolts weigh nothing.");
            VanillaLoot = Config.Bind("Loot", "KeepVanillaLootStacks", true, "Loot, shops, rewards and drops roll and split ammo / arrows with the original stack sizes, so what you find is the same as without the mod. Off: loot can come in bigger stacks.");
            VerboseLog = Config.Bind("Debug", "VerboseLog", false, "Extra detail in BepInEx\\AmmoStacks.log: every hook attached, loot call counts per method, and every ammo loot roll. Turn on when reporting a problem.");

            ALog.Verbose = VerboseLog.Value;
            ALog.Open(Version);

            string gameVer = "?", unityVer = "?";
            try { gameVer = Application.version; } catch { }
            try { unityVer = Application.unityVersion; } catch { }
            ALog.Info("start", $"AmmoStacks {Version} loading. Game {gameVer}, Unity {unityVer}, BepInEx {typeof(BasePlugin).Assembly.GetName().Version}, .NET {Environment.Version}");
            ALog.Info("start", $"Config: AmmoStackSize={AmmoStack.Value}, ArrowStackSize={ArrowStack.Value}, RemoveAmmoWeight={AmmoWeightless.Value}, RemoveArrowWeight={ArrowWeightless.Value}, KeepVanillaLootStacks={VanillaLoot.Value}, VerboseLog={VerboseLog.Value}");
            if (AmmoStack.Value < 1 || ArrowStack.Value < 1)
                ALog.Warn("start", "A stack size in the config is below 1 - items keep their original stack size.");

            var harmony = new Harmony(Guid);
            try { harmony.PatchAll(typeof(InitPatch)); ALog.Info("start", "Hooked ItemManager.Init (stacks applied when the item list loads)."); }
            catch (Exception e) { ALog.Error("start", "Couldn't hook ItemManager.Init - stacks are still applied by the update loop within a few seconds", e); }

            if (VanillaLoot.Value) HookLoot(harmony);
            else ALog.Info("start", "KeepVanillaLootStacks is off: loot uses the bigger stacks too. No loot hooks.");

            try
            {
                harmony.Patch(AccessTools.Method(typeof(ItemManager), nameof(ItemManager.GetRandomDataByAttr)),
                    postfix: new HarmonyMethod(typeof(RollCheck), nameof(RollCheck.Postfix)) { priority = Priority.Last });
                ALog.Info("start", "Hooked the ammo loot roll check (warns if loot ever comes out bigger than the vanilla stack).");
            }
            catch (Exception e) { ALog.Error("start", "Couldn't hook the ammo loot roll check (the mod still works, there's just no check)", e); }

            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<AmmoStacksBehaviour>();
                AddComponent<AmmoStacksBehaviour>();
                ALog.Info("start", "Update loop started.");
            }
            catch (Exception e) { ALog.Error("start", "Couldn't start the update loop - stacks are only applied when ItemManager.Init runs, and there's no stuck-loot recovery", e); }

            ALog.Info("start", VanillaLoot.Value
                ? $"AmmoStacks {Version} loaded. Loot hooks: {HooksOk + HooksPlain}/{HooksWanted} attached ({HooksPlain} without call tracking), {HooksMissing} not found, {HooksFailed} failed. Log file: BepInEx\\AmmoStacks.log"
                : $"AmmoStacks {Version} loaded. Log file: BepInEx\\AmmoStacks.log");
        }

        static void HookLoot(Harmony harmony)
        {
            var preTracked = new HarmonyMethod(typeof(LootScope).GetMethod("EnterTracked", BindingFlags.Static | BindingFlags.NonPublic));
            var prePlain = new HarmonyMethod(typeof(LootScope).GetMethod("Enter", BindingFlags.Static | BindingFlags.NonPublic));
            var post = new HarmonyMethod(typeof(LootScope).GetMethod("Exit", BindingFlags.Static | BindingFlags.NonPublic));
            var missing = new List<string>();

            foreach (var target in LootScope.Targets)
            {
                int cut = target.LastIndexOf('.');
                string typeName = target.Substring(0, cut), method = target.Substring(cut + 1);
                Type type = null;
                try { type = AccessTools.TypeByName(typeName); } catch { }
                var methods = type == null ? new List<MethodInfo>() : AccessTools.GetDeclaredMethods(type).FindAll(m => m.Name == method && !m.IsAbstract && !m.IsGenericMethod);
                if (methods.Count == 0)
                {
                    HooksMissing++;
                    missing.Add(type == null ? target + " (type gone)" : target);
                    continue;
                }
                foreach (var m in methods)
                {
                    HooksWanted++;
                    string sig = $"{target}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})";
                    try
                    {
                        harmony.Patch(m, prefix: preTracked, postfix: post);
                        HooksOk++;
                        LootScope.Hooked.Add(sig);
                        ALog.Debug("start", "  hooked " + sig);
                    }
                    catch (Exception e1)
                    {
                        // Call tracking needs __originalMethod; if that's what failed, hook without it.
                        try
                        {
                            harmony.Unpatch(m, HarmonyPatchType.All, Guid);
                            harmony.Patch(m, prefix: prePlain, postfix: post);
                            HooksPlain++;
                            LootScope.Hooked.Add(sig);
                            ALog.Warn("start", $"Hooked {sig} without call tracking (first try failed: {e1.Message})");
                        }
                        catch (Exception e2)
                        {
                            HooksFailed++;
                            ALog.Error("start", $"Couldn't hook {sig} - loot from it may come in bigger ammo stacks", e2);
                        }
                    }
                }
            }
            if (missing.Count > 0)
                ALog.Warn("start", $"{missing.Count} loot method(s) not in this game version (game update?) - loot from them may come in bigger ammo stacks: {string.Join(", ", missing)}");
        }
    }

    // One ammo / arrow item definition we changed.
    internal sealed class Entry
    {
        internal ItemAttr Attr;
        internal IntPtr Ptr;
        internal int Id;
        internal string Name;
        internal bool Arrow;
        internal int OrigStack, ModStack;
        internal float OrigWeight, ModWeight;
        // loot roll check
        internal int Rolls, MaxRoll, OverVanilla;
    }

    internal static class Stacks
    {
        static readonly Dictionary<IntPtr, Entry> _byPtr = new Dictionary<IntPtr, Entry>();
        internal static readonly List<Entry> Entries = new List<Entry>();
        static int _lastCount = -1;
        internal static int Resets;

        internal static Entry Find(IntPtr p)
        {
            lock (_byPtr) return _byPtr.TryGetValue(p, out var e) ? e : null;
        }

        // Finds every ammo / arrow definition (base game and mods) and gives it the raised stack and no weight.
        // Only runs when no loot method is running. Cheap when nothing changed.
        internal static void Apply(string why)
        {
            if (LootScope.Depth != 0) { ALog.Debug("items", $"[{why}] skipped: loot is being generated"); return; }
            var im = ItemManager.instance;
            if (im == null) return;
            var dic = im.itemAttrDic;
            if (dic == null) return;

            int added = 0, reset = 0, seen = 0, total = 0, bad = 0;
            foreach (var kv in dic)
            {
                total++;
                ItemAttr a = null;
                try
                {
                    a = kv.Value;
                    if (a == null) continue;
                    var type = a.itemType;
                    bool ammo = type == ItemType.Ammo, arrow = type == ItemType.Arrow;
                    if (!ammo && !arrow) continue;
                    seen++;
                    IntPtr p = a.Pointer;
                    Entry e;
                    lock (_byPtr) _byPtr.TryGetValue(p, out e);
                    if (e == null)
                    {
                        e = new Entry { Attr = a, Ptr = p, Id = a.itemId, Name = NameOf(a), Arrow = arrow, OrigStack = a.stackNumber, OrigWeight = a.weight };
                        lock (_byPtr) _byPtr[p] = e;
                        Entries.Add(e);
                        added++;
                        if (e.OrigStack <= 0) ALog.Warn("items", $"{Label(e)} has stack size {e.OrigStack} in the game data - stack left as is.");
                    }
                    else if (a.stackNumber != e.ModStack || a.weight != e.ModWeight)
                    {
                        // Something put new values in since we set ours: the game reloading item data, or another mod.
                        ALog.Warn("watch", $"{Label(e)} changed by something else: stack {e.ModStack} -> {a.stackNumber}, weight {e.ModWeight:0.###} -> {a.weight:0.###}. Taking those as the new originals and re-applying (game reload or another mod editing ammo).");
                        e.OrigStack = a.stackNumber;
                        e.OrigWeight = a.weight;
                        reset++; Resets++;
                    }
                    else continue;

                    int want = arrow ? Plugin.ArrowStack.Value : Plugin.AmmoStack.Value;
                    e.ModStack = e.OrigStack <= 0 ? e.OrigStack : Math.Max(e.OrigStack, want);
                    e.ModWeight = (arrow ? Plugin.ArrowWeightless.Value : Plugin.AmmoWeightless.Value) ? 0f : e.OrigWeight;
                    a.stackNumber = e.ModStack;
                    a.weight = e.ModWeight;

                    // read back: catches a write that didn't stick
                    if (a.stackNumber != e.ModStack || a.weight != e.ModWeight)
                        ALog.Error("items", $"{Label(e)}: wrote stack {e.ModStack} / weight {e.ModWeight} but read back {a.stackNumber} / {a.weight}");
                    else
                        ALog.Info("items", $"{Label(e)}: stack {e.OrigStack} -> {e.ModStack}, weight {e.OrigWeight:0.###} -> {e.ModWeight:0.###}");
                }
                catch (Exception ex)
                {
                    bad++;
                    ALog.ErrorThrottled("apply|" + SafeId(a), 300, "items", $"Couldn't read/set item #{SafeId(a)}", ex);
                }
            }

            if (added > 0 || reset > 0 || seen != _lastCount || bad > 0)
            {
                _lastCount = seen;
                int ammoN = Entries.Count(x => !x.Arrow), arrowN = Entries.Count(x => x.Arrow);
                ALog.Info("items", $"[{why}] {seen} ammo/arrow items out of {total} ({added} new, {reset} re-applied, {bad} failed). Tracking {ammoN} ammo + {arrowN} arrow types.");
            }
        }

        // Loot is being made: original stack sizes.
        internal static void SetVanilla()
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                try { e.Attr.stackNumber = e.OrigStack; }
                catch (Exception ex) { ALog.ErrorThrottled("vanilla|" + e.Id, 300, "loot", $"Couldn't set vanilla stack on {Label(e)}", ex); }
            }
        }

        // Loot is done: raised stack sizes.
        internal static void SetModded()
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                try { e.Attr.stackNumber = e.ModStack; }
                catch (Exception ex) { ALog.ErrorThrottled("modded|" + e.Id, 300, "loot", $"Couldn't put the raised stack back on {Label(e)}", ex); }
            }
        }

        // Confirms our values are in place right now (outside loot). Returns how many are off.
        internal static int Verify(out string detail)
        {
            int off = 0; var sb = new System.Text.StringBuilder();
            foreach (var e in Entries)
            {
                try
                {
                    int s = e.Attr.stackNumber; float w = e.Attr.weight;
                    if (s != e.ModStack || w != e.ModWeight)
                    {
                        off++;
                        if (off <= 5) sb.Append($" {Label(e)} is stack {s}/weight {w:0.###} (want {e.ModStack}/{e.ModWeight:0.###});");
                    }
                }
                catch { off++; }
            }
            detail = sb.ToString();
            return off;
        }

        internal static string Label(Entry e) => $"{(e.Arrow ? "arrow" : "ammo")} #{e.Id} '{e.Name}'";

        static string NameOf(ItemAttr a)
        {
            try { var n = a.ItemName_EN; if (!string.IsNullOrEmpty(n)) return n; } catch { }
            try { return a.itemName ?? "?"; } catch { return "?"; }
        }

        static string SafeId(ItemAttr a)
        {
            try { return a == null ? "null" : a.itemId.ToString(); } catch { return "?"; }
        }
    }

    // Wraps the game's loot code. Nested calls share one scope.
    internal static class LootScope
    {
        static int _depth, _maxDepth;
        internal static int Depth => Volatile.Read(ref _depth);
        internal static int MaxDepth => _maxDepth;
        internal static long Scopes;            // outermost loot calls
        internal static int StuckResets;
        internal static float StuckSince = -1f;
        internal static readonly List<string> Hooked = new List<string>();
        static readonly Dictionary<string, long> _calls = new Dictionary<string, long>();

        // Every method below decides how much ammo / how big a stack loot gets (found by disassembling
        // the game: each one either rolls an ammo count or splits loot by ItemAttr.stackNumber).
        internal static readonly string[] Targets =
        {
            // the roll itself + the generic loot pickers
            "ItemManager.GetRandomDataByAttr",
            "ItemManager.GetRandomLootItemData",
            "ItemManager.GetRandomLootItemData_Room",
            "ItemManager.GetRandomLootItemData_Field",
            "ItemManager.GetRandomCoastItemData",
            "ItemManager.FinishLootItem",
            // containers / furniture
            "TerrainObject_Furniture_Inventory.GenerateLootItems",
            "TerrainObject_Furniture_Inventory.OnLootFirstTime",
            "TerrainObject_Furniture_Inventory.GenerateOneSpecifiedItem",
            "TerrainObject_Furniture_Inventory.RollBoostFillerItem",
            "TerrainObject_Furniture_Inventory.EnsureLootGenerated",
            "TerrainObject_Furniture_Inventory_LootObject.OnLootFirstTime",
            "TerrainObject_Furniture_Inventory_Shelf.GenerateLootItems",
            "TerrainObject_Furniture_Inventory_OilTank.GenerateLootItems",
            "TerrainObject_Production_Fridge.GenerateLootItems",
            "TerrainObject_Production_Fridge.EnsureLootGeneratedNet",
            "TerrainObject_Enterable_Container.GenerateInsideLoot",
            "TerrainObject_NppCoreVault.OnLootFirstTime",
            // corpses, heaps, vehicles, ground
            "TerrainObject_HordeCorpseHeap.GenerateLootItems",
            "TerrainObject_HordeCorpseHeap.GenerateHeapLoot",
            "TerrainObject_HordeCorpseHeap.EnsureLootGenerated",
            "TerrainObject_Corpse.GenerateLootItems",
            "BasicVehicle.GenerateLootItems",
            "BasicVehicle.GenerateGuaranteedLootItems",
            "BuildingDataGenerator.GenerateGroundItems",
            "BuildingWallDamageSystem.DropWallLoot",
            "BuildingWallDamageSystem.SpawnColumnLoot",
            "VoxelDamageSystem.DropLootItem",
            // dismantling
            "TerrainObject_DestroyedMech.GenerateDismantleItems",
            "TerrainObject_DestroyedMech.DropDismantleItem",
            "TerrainObject_SupplyBox.GenerateDismantleItems",
            "TerrainObject_SupplyBox.DropDismantleItem",
            "TerrainObject_CorpsePile.GenerateDismantleItems",
            "TerrainObject_CorpsePile.DropDismantleItem",
            // traders, NPC gear, backpacks / clothes that come with loot
            "NPCData_NPCInteract.GenerateMerchandiseItems",
            "NPCData_NPCInteract.GenerateWandererShopItems",
            "NPCData_CampDoctorInteract.GenerateMerchandiseItems",
            "GOAPAgent_Survivor.AddSpawnAmmo",
            "CharacterData.RandomBackPack",
            "BasicItemUI.GenerateLoot_BackPack",
            "BasicItemUI.GenerateLoot_Clothing",
            "MechController.GetRandomMechWeaponData",
            "SpiderRobotController.GetRandomMechWeaponData",
            // rewards
            "TerrainObject_TaskNPC.GiveEventRewards",
            "ZedZone.HordeMode.HordeRewardTable.SpawnArmoryBox",
            "ZedZone.HordeMode.HordeRewardTable.SpawnMaterialBox",
            "ZedZone.HordeMode.HordeRewardTable.SpawnToolBox",
            "ZedZone.HordeMode.HordeSupplyPack.RollInto",
            "FinalBossRewards.FillWreck",
            "FactoryMechItems.FillWreckParts",
            "SecondIslandAirport.Give",
            "SecondIslandArmory.Give",
        };

        // Prefix with call tracking (used when Harmony can give us the method).
        static void EnterTracked(MethodBase __originalMethod)
        {
            Enter();
            try
            {
                string name = __originalMethod == null ? "?" : __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
                long n;
                lock (_calls) { _calls.TryGetValue(name, out n); _calls[name] = ++n; }
                if (n == 1) ALog.Info("loot", $"First call of {name} - ammo here uses vanilla stacks (depth {Depth}).");
                else if (ALog.Verbose && (n == 10 || n == 100 || n == 1000 || n % 10000 == 0))
                    ALog.Debug("loot", $"{name} called {n} times");
            }
            catch (Exception e) { ALog.ErrorThrottled("enterT", 60, "error", "Loot hook call tracking failed", e); }
        }

        // Prefix without call tracking.
        static void Enter()
        {
            try
            {
                int d = Interlocked.Increment(ref _depth);
                if (d > _maxDepth) _maxDepth = d;
                if (d == 1)
                {
                    Interlocked.Increment(ref Scopes);
                    Stacks.SetVanilla();
                }
                else if (d > 64)
                    ALog.WarnThrottled("deep", 60, "loot", $"Loot hooks nested {d} deep - an exit was probably missed (will reset once loot stops).");
            }
            catch (Exception e) { ALog.ErrorThrottled("enter", 60, "error", "Loot hook (enter) failed", e); }
        }

        static void Exit()
        {
            try
            {
                int d = Interlocked.Decrement(ref _depth);
                if (d == 0) Stacks.SetModded();
                else if (d < 0)
                {
                    Interlocked.Exchange(ref _depth, 0);
                    Stacks.SetModded();
                    ALog.WarnThrottled("negdepth", 60, "loot", "Loot hook exit without a matching enter - reset.");
                }
            }
            catch (Exception e) { ALog.ErrorThrottled("exit", 60, "error", "Loot hook (exit) failed", e); }
        }

        // Called from the update loop if a scope never closed (the game threw inside a loot method).
        internal static void ForceClose(float stuckFor)
        {
            int d = Interlocked.Exchange(ref _depth, 0);
            Stacks.SetModded();
            StuckResets++;
            ALog.Warn("loot", $"Loot scope stuck open for {stuckFor:0}s at depth {d} (the game probably threw inside a loot method) - raised stacks restored. Loot calls so far: {CallSummary(8)}");
        }

        internal static int MethodsFired { get { lock (_calls) return _calls.Count; } }

        internal static string CallSummary(int top)
        {
            lock (_calls)
            {
                if (_calls.Count == 0) return "none";
                return string.Join(", ", _calls.OrderByDescending(kv => kv.Value).Take(top).Select(kv => $"{kv.Key} x{kv.Value}"));
            }
        }
    }

    // Checks every ammo/arrow loot roll against the vanilla stack size.
    internal static class RollCheck
    {
        internal static long Rolls, Over;

        internal static void Postfix(ItemAttr __0, ItemData __result)
        {
            try
            {
                if (__0 == null || __result == null) return;
                var e = Stacks.Find(__0.Pointer);
                if (e == null) return;
                float n = __result.itemNumberFloat;
                Interlocked.Increment(ref Rolls);
                e.Rolls++;
                if (n > e.MaxRoll) e.MaxRoll = (int)n;
                if (Plugin.VanillaLoot.Value && e.OrigStack > 1 && n > e.OrigStack)
                {
                    Interlocked.Increment(ref Over);
                    e.OverVanilla++;
                    ALog.WarnThrottled("over|" + e.Id, 30, "loot", $"Loot roll for {Stacks.Label(e)} came out {n} - bigger than the vanilla stack {e.OrigStack}. Stack size during the roll: {SafeStack(e)}, loot depth {LootScope.Depth}. (The vanilla-loot hook missed this roll.)");
                }
                else if (ALog.Verbose)
                    ALog.Debug("loot", $"roll {Stacks.Label(e)}: {n} (vanilla cap {e.OrigStack})");
            }
            catch (Exception ex) { ALog.ErrorThrottled("rollcheck", 300, "error", "Loot roll check failed", ex); }
        }

        static string SafeStack(Entry e) { try { return e.Attr.stackNumber.ToString(); } catch { return "?"; } }
    }

    [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.Init))]
    internal static class InitPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            try { Stacks.Apply("ItemManager.Init"); }
            catch (Exception e) { ALog.Error("error", "Applying stack sizes after ItemManager.Init failed", e); }
        }
    }

    public class AmmoStacksBehaviour : MonoBehaviour
    {
        public AmmoStacksBehaviour(IntPtr ptr) : base(ptr) { }

        float _nextApply, _nextStatus = 60f, _nextVerify;
        bool _envLogged, _noItemsWarned;
        int _lastErrors, _lastWarnings;

        void Update()
        {
            float now;
            try { now = Time.unscaledTime; } catch { return; }

            try
            {
                if (!_envLogged && now > 2f) { _envLogged = true; LogEnvironment(); }

                if (LootScope.Depth != 0)
                {
                    // Loot methods all finish within a frame. If one is still "open" seconds later it threw
                    // before our postfix ran: put the big stacks back.
                    if (LootScope.StuckSince < 0f) LootScope.StuckSince = now;
                    else if (now - LootScope.StuckSince > 10f)
                    {
                        LootScope.ForceClose(now - LootScope.StuckSince);
                        LootScope.StuckSince = -1f;
                    }
                    return;
                }
                LootScope.StuckSince = -1f;

                if (now >= _nextApply)
                {
                    _nextApply = now + 3f;
                    Stacks.Apply("check");   // picks up mod items added later and survives the game reloading item data
                }

                if (now >= _nextVerify && Stacks.Entries.Count > 0)
                {
                    _nextVerify = now + 30f;
                    int off = Stacks.Verify(out var detail);
                    if (off > 0) ALog.WarnThrottled("verify", 120, "watch", $"{off} item(s) don't have our stack/weight right now (outside loot):{detail} Re-applying.");
                }

                if (!_noItemsWarned && Stacks.Entries.Count == 0 && now > 120f && ItemManager.instance != null)
                {
                    _noItemsWarned = true;
                    ALog.Warn("items", "ItemManager exists but no ammo/arrow items were found after 2 minutes - nothing is being changed. (Item list empty, or the game renamed ItemType.Ammo/Arrow.)");
                }

                // status: every 5 min, and within a minute of a new error/warning
                bool news = ALog.Errors != _lastErrors || ALog.Warnings != _lastWarnings;
                if (now >= _nextStatus || (news && now >= _nextStatus - 240f))
                {
                    _nextStatus = now + 300f;
                    _lastErrors = ALog.Errors; _lastWarnings = ALog.Warnings;
                    ALog.Info("status", Status());
                }
            }
            catch (Exception e) { ALog.ErrorThrottled("update", 60, "error", "Update loop failed", e); _nextApply = now + 30f; }
        }

        void OnDestroy()
        {
            try { ALog.Info("status", "Shutting down. " + Status()); } catch { }
        }

        internal static string Status()
        {
            var es = Stacks.Entries;
            int ammo = es.Count(x => !x.Arrow), arrows = es.Count(x => x.Arrow);
            string overItems = string.Join(", ", es.Where(x => x.OverVanilla > 0).Select(x => $"{Stacks.Label(x)} x{x.OverVanilla}"));
            return $"items {ammo} ammo + {arrows} arrow | " +
                   (Plugin.VanillaLoot.Value
                       ? $"loot hooks {Plugin.HooksOk + Plugin.HooksPlain}/{Plugin.HooksWanted} attached, {LootScope.MethodsFired} fired, {LootScope.Scopes} loot calls, max nesting {LootScope.MaxDepth}, stuck resets {LootScope.StuckResets} | "
                       : "vanilla loot off | ") +
                   $"ammo rolls {RollCheck.Rolls}, over vanilla {RollCheck.Over}{(overItems.Length > 0 ? " (" + overItems + ")" : "")} | " +
                   $"external resets {Stacks.Resets} | errors {ALog.Errors}, warnings {ALog.Warnings}" +
                   (ALog.Verbose ? $" | top loot calls: {LootScope.CallSummary(6)}" : "");
        }

        static void LogEnvironment()
        {
            try
            {
                var plugins = IL2CPPChainloader.Instance?.Plugins;
                if (plugins == null) return;
                ALog.Info("start", $"BepInEx plugins loaded ({plugins.Count}): {string.Join(", ", plugins.Values.Select(p => $"{p.Metadata.Name} {p.Metadata.Version} [{p.Metadata.GUID}]"))}");
                var others = plugins.Values.Where(p => p.Metadata.GUID != Plugin.Guid &&
                    (p.Metadata.Name + p.Metadata.GUID).IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0).Select(p => p.Metadata.Name).ToList();
                if (others.Count > 0)
                    ALog.Warn("start", $"Other stack-related mod(s) loaded: {string.Join(", ", others)} - if ammo stacks act strange, try without them.");
            }
            catch (Exception e) { ALog.Error("start", "Couldn't list loaded plugins", e); }
        }
    }
}
