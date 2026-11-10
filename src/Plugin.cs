using System;
using System.Collections.Generic;
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
    [BepInPlugin(Guid, "AmmoStacks (100-round ammo & arrow stacks, no weight)", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "girthy_mods.zedzone.ammostacks";
        public const string Version = "1.0.0";

        internal static ManualLogSource L;
        internal static ConfigEntry<int> AmmoStack, ArrowStack;
        internal static ConfigEntry<bool> AmmoWeightless, ArrowWeightless, VanillaLoot, VerboseLog;

        public override void Load()
        {
            L = Log;
            AmmoStack = Config.Bind("Stacks", "AmmoStackSize", 100, "Max stack size for bullets / shells (ItemType Ammo). Never lowers an item below its original stack size.");
            ArrowStack = Config.Bind("Stacks", "ArrowStackSize", 100, "Max stack size for arrows / bolts (ItemType Arrow). Never lowers an item below its original stack size.");
            AmmoWeightless = Config.Bind("Weight", "RemoveAmmoWeight", true, "Bullets / shells weigh nothing.");
            ArrowWeightless = Config.Bind("Weight", "RemoveArrowWeight", true, "Arrows / bolts weigh nothing.");
            VanillaLoot = Config.Bind("Loot", "KeepVanillaLootStacks", true, "Loot, shops, rewards and drops roll and split ammo / arrows with the original stack sizes, so what you find is the same as without the mod. Off: loot can come in bigger stacks.");
            VerboseLog = Config.Bind("Debug", "VerboseLog", false, "Log (every 50th time) when loot is made with the vanilla ammo stacks.");

            string gameVer = "?";
            try { gameVer = Application.version; } catch { }
            L.LogInfo($"AmmoStacks {Version} loading (game {gameVer}). Ammo stack {AmmoStack.Value}, arrow stack {ArrowStack.Value}, ammo weightless {AmmoWeightless.Value}, arrows weightless {ArrowWeightless.Value}, vanilla loot stacks {VanillaLoot.Value}.");

            var harmony = new Harmony(Guid);
            try { harmony.PatchAll(typeof(InitPatch)); }
            catch (Exception e) { L.LogError("Couldn't hook ItemManager.Init (stacks are still applied by the update loop): " + e); }

            int hooked = 0, missing = 0, failed = 0;
            if (VanillaLoot.Value)
            {
                var pre = new HarmonyMethod(typeof(LootScope).GetMethod("Enter", BindingFlags.Static | BindingFlags.NonPublic));
                var post = new HarmonyMethod(typeof(LootScope).GetMethod("Exit", BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var target in LootScope.Targets)
                {
                    int cut = target.LastIndexOf('.');
                    string typeName = target.Substring(0, cut), method = target.Substring(cut + 1);
                    var type = AccessTools.TypeByName(typeName);
                    var methods = type == null ? new List<MethodInfo>() : AccessTools.GetDeclaredMethods(type).FindAll(m => m.Name == method && !m.IsAbstract && !m.IsGenericMethod);
                    if (methods.Count == 0) { missing++; L.LogWarning($"Loot method {target} not found (game update?) - loot from it may come in bigger ammo stacks."); continue; }
                    foreach (var m in methods)
                    {
                        try { harmony.Patch(m, prefix: pre, postfix: post); hooked++; }
                        catch (Exception e) { failed++; L.LogError($"Couldn't hook {target}: {e.Message}"); }
                    }
                }
            }

            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<AmmoStacksBehaviour>();
                AddComponent<AmmoStacksBehaviour>();
            }
            catch (Exception e) { L.LogError("Couldn't start the update loop: " + e); }

            L.LogInfo(VanillaLoot.Value
                ? $"AmmoStacks {Version} loaded. Loot methods hooked: {hooked}, not found: {missing}, failed: {failed}."
                : $"AmmoStacks {Version} loaded. KeepVanillaLootStacks is off: loot uses the bigger stacks too.");
        }
    }

    // One ammo / arrow item definition we changed.
    internal sealed class Entry
    {
        internal ItemAttr Attr;
        internal IntPtr Ptr;
        internal int OrigStack, ModStack;
        internal float OrigWeight, ModWeight;
    }

    internal static class Stacks
    {
        static readonly Dictionary<IntPtr, Entry> _byPtr = new Dictionary<IntPtr, Entry>();
        internal static readonly List<Entry> Entries = new List<Entry>();
        static int _lastCount = -1;

        static bool IsAmmo(ItemAttr a) => a.itemType == ItemType.Ammo;
        static bool IsArrow(ItemAttr a) => a.itemType == ItemType.Arrow;

        // Finds every ammo / arrow definition (base game and mods) and gives it the raised stack and no weight.
        // Safe to call any time no loot method is running. Cheap when nothing changed.
        internal static void Apply(string why)
        {
            if (LootScope.Depth != 0) return;
            var im = ItemManager.instance;
            if (im == null) return;
            var dic = im.itemAttrDic;
            if (dic == null) return;

            int added = 0, reset = 0, seen = 0;
            foreach (var kv in dic)
            {
                var a = kv.Value;
                if (a == null) continue;
                bool ammo = IsAmmo(a), arrow = IsArrow(a);
                if (!ammo && !arrow) continue;
                seen++;
                IntPtr p = a.Pointer;
                if (!_byPtr.TryGetValue(p, out var e))
                {
                    e = new Entry { Attr = a, Ptr = p, OrigStack = a.stackNumber, OrigWeight = a.weight };
                    _byPtr[p] = e;
                    Entries.Add(e);
                    added++;
                }
                else if (a.stackNumber != e.ModStack || a.weight != e.ModWeight)
                {
                    // The game (or a mod) put new values in since we last set ours: take them as the new originals.
                    e.OrigStack = a.stackNumber;
                    e.OrigWeight = a.weight;
                    reset++;
                }
                else continue;

                int want = ammo ? Plugin.AmmoStack.Value : Plugin.ArrowStack.Value;
                e.ModStack = Math.Max(e.OrigStack, want);
                e.ModWeight = (ammo ? Plugin.AmmoWeightless.Value : Plugin.ArrowWeightless.Value) ? 0f : e.OrigWeight;
                a.stackNumber = e.ModStack;
                a.weight = e.ModWeight;
                Plugin.L.LogInfo($"  {(ammo ? "ammo " : "arrow")} #{a.itemId} {Name(a)}: stack {e.OrigStack} -> {e.ModStack}, weight {e.OrigWeight:0.###} -> {e.ModWeight:0.###}");
            }
            if (added > 0 || reset > 0 || seen != _lastCount)
            {
                _lastCount = seen;
                Plugin.L.LogInfo($"[{why}] {seen} ammo/arrow items set ({added} new, {reset} refreshed).");
            }
        }

        // Loot is being made: original stack sizes.
        internal static void SetVanilla()
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                try { e.Attr.stackNumber = e.OrigStack; } catch { }
            }
        }

        // Loot is done: raised stack sizes.
        internal static void SetModded()
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                try { e.Attr.stackNumber = e.ModStack; } catch { }
            }
        }

        static string Name(ItemAttr a)
        {
            try { var n = a.ItemName_EN; if (!string.IsNullOrEmpty(n)) return n; } catch { }
            try { return a.itemName; } catch { return "?"; }
        }
    }

    // Wraps the game's loot code. Nested calls share one scope.
    internal static class LootScope
    {
        static int _depth;
        internal static int Depth => Volatile.Read(ref _depth);
        internal static float StuckSince = -1f;

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

        static int _entered;

        static void Enter()
        {
            if (Interlocked.Increment(ref _depth) == 1)
            {
                Stacks.SetVanilla();
                if (Plugin.VerboseLog.Value && ++_entered % 50 == 1)
                    Plugin.L.LogInfo($"loot generated with vanilla ammo stacks ({_entered} times so far)");
            }
        }

        static void Exit()
        {
            int d = Interlocked.Decrement(ref _depth);
            if (d == 0) Stacks.SetModded();
            else if (d < 0) Interlocked.Exchange(ref _depth, 0);
        }

        // Called from the update loop if a scope never closed (the game threw inside a loot method).
        internal static void ForceClose()
        {
            Interlocked.Exchange(ref _depth, 0);
            Stacks.SetModded();
        }
    }

    [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.Init))]
    internal static class InitPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            try { Stacks.Apply("ItemManager.Init"); }
            catch (Exception e) { Plugin.L.LogError("Applying stack sizes after ItemManager.Init failed: " + e); }
        }
    }

    public class AmmoStacksBehaviour : MonoBehaviour
    {
        public AmmoStacksBehaviour(IntPtr ptr) : base(ptr) { }

        float _next;

        void Update()
        {
            try
            {
                float now = Time.unscaledTime;
                if (LootScope.Depth != 0)
                {
                    // Loot methods all finish within a frame. If one is still "open" seconds later it threw
                    // before our postfix ran: put the big stacks back.
                    if (LootScope.StuckSince < 0f) LootScope.StuckSince = now;
                    else if (now - LootScope.StuckSince > 10f)
                    {
                        Plugin.L.LogWarning("A loot method didn't finish - restoring the raised stack sizes.");
                        LootScope.ForceClose();
                        LootScope.StuckSince = -1f;
                    }
                    return;
                }
                LootScope.StuckSince = -1f;
                if (now < _next) return;
                _next = now + 3f;
                Stacks.Apply("check");   // picks up mod items added later and survives the game reloading item data
            }
            catch (Exception e) { Plugin.L.LogError("Update failed: " + e.Message); _next = Time.unscaledTime + 30f; }
        }
    }
}
