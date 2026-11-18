using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace InteropFixes.Modules.EpicLootCraftyBoxes;

internal static class EpicLootCraftyBoxesModule
{
    internal const string EpicLootGuid = "randyknapp.mods.epicloot";
    internal const string CraftyBoxesGuid = "Azumatt.AzuCraftyBoxes";
    private static Harmony? _harmony;
    private static MethodInfo[] _actions = Array.Empty<MethodInfo>();
    private static MethodInfo[] _observed = Array.Empty<MethodInfo>();
    private static MethodInfo[] _providerContracts = Array.Empty<MethodInfo>();
    private static Func<object, ItemDrop.ItemData?> _selectedItem = null!;
    private static Action<object> _cancel = null!;
    private static Type? _rune;
    private static bool _coverageFailed;
    private static bool _active;

    // Called after optional plugins have completed Awake/Start. No third-party AssemblyRef
    // or JIT signature exists: absent dependencies cannot prevent another module loading.
    internal static bool TryInstall(Harmony harmony)
    {
        if (_harmony != null) return _active && ReferenceEquals(_harmony, harmony);
        if (!Chainloader.PluginInfos.TryGetValue(EpicLootGuid, out var epic) ||
            !Chainloader.PluginInfos.TryGetValue(CraftyBoxesGuid, out var crafty)) return false;
        _harmony = harmony;
        try
        {
            Assembly epicAssembly = epic.Instance.GetType().Assembly;
            Assembly craftyAssembly = crafty.Instance.GetType().Assembly;
            ContainerBridge.Initialize(craftyAssembly);
            ResolveActions(epicAssembly);
            MethodInfo load = BridgeContracts.RequireMethod(typeof(Container), "Load", typeof(bool));
            MethodInfo save = BridgeContracts.RequireMethod(typeof(Container), "Save", typeof(void));
            MethodInfo stop = BridgeContracts.RequireMethod(typeof(ZNet), "StopAll", typeof(void), typeof(bool));
            BridgeContracts.RequireStatic(load, false);
            BridgeContracts.RequireStatic(save, false);
            BridgeContracts.RequireStatic(stop, false);
            BridgeContracts.RequireCalls(load, "Inventory", "Load");
            BridgeContracts.RequireCalls(save, "Inventory", "Save");
            ValidateProvider(craftyAssembly);
            _observed = new[] { ContainerBridge.GetItemsMethod, load, save, stop };
            if (HasCompetingMutators()) throw new InvalidOperationException("A required storage/action method has an unsupported competing prefix or transpiler.");
            // All member/call contracts are resolved before the first mutation.
            for (int index = 0; index < _actions.Length; index++)
                harmony.Patch(_actions[index], Hook(index == _actions.Length - 1 ? nameof(BeforeAugment) : nameof(BeforePanel)),
                    finalizer: Hook(nameof(AfterAction)));
            harmony.Patch(_observed[0], postfix: Hook(nameof(AfterProviderItems)));
            harmony.Patch(load, postfix: Hook(nameof(AfterLoad)));
            harmony.Patch(save, postfix: Hook(nameof(AfterSave)));
            harmony.Patch(stop, postfix: Hook(nameof(AfterStop)));
            if (!HasCompleteCoverage()) throw new InvalidOperationException("Bridge patch installation is incomplete.");
            _active = true;
            InteropFixesPlugin.ModLogger.LogInfo("EpicLoot/CraftyBoxes: guarded container enchanting persistence enabled on this client.");
            return true;
        }
        catch (Exception error)
        {
            Stop();
            InteropFixesPlugin.ModLogger.LogWarning("EpicLoot/CraftyBoxes bridge disabled: contract or installation failed. " +
                "Container enchanting is not protected; move equipment to the player inventory.\n" + error);
            return false;
        }
    }

    private static void ResolveActions(Assembly epic)
    {
        Type panelBase = BridgeContracts.RequireType(epic, "EpicLoot_UnityLib.EnchantingTableUIPanelBase");
        Type list = BridgeContracts.RequireType(epic, "EpicLoot_UnityLib.MultiSelectItemList");
        Type element = BridgeContracts.RequireType(epic, "EpicLoot_UnityLib.IListElement");
        FieldInfo available = BridgeContracts.RequireField(panelBase, "AvailableItems", list, false);
        MethodInfo getItem = BridgeContracts.RequireMethod(element, "GetItem", typeof(ItemDrop.ItemData));
        MethodInfo cancel = BridgeContracts.RequireMethod(panelBase, "Cancel", typeof(void));
        BridgeContracts.RequireStatic(getItem, false);
        BridgeContracts.RequireStatic(cancel, false);
        MethodInfo[] selections = list.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "GetSingleSelectedItem" && m.IsGenericMethodDefinition &&
                m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0).ToArray();
        if (selections.Length != 1) throw new MissingMethodException(list.FullName, "GetSingleSelectedItem<T>()");
        MethodInfo select = selections[0].MakeGenericMethod(element);
        Type tupleType = typeof(Tuple<,>).MakeGenericType(element, typeof(int));
        if (select.ReturnType != tupleType) throw new InvalidOperationException("Unexpected selected item tuple.");
        ParameterExpression panel = Expression.Parameter(typeof(object), "panel");
        ParameterExpression items = Expression.Variable(list, "items");
        ParameterExpression tuple = Expression.Variable(tupleType, "selected");
        Expression noItem = Expression.Constant(null, typeof(ItemDrop.ItemData));
        _selectedItem = Expression.Lambda<Func<object, ItemDrop.ItemData?>>(Expression.Block(new[] { items, tuple },
            Expression.Assign(items, Expression.Field(Expression.Convert(panel, panelBase), available)),
            Expression.Condition(Expression.ReferenceEqual(items, Expression.Constant(null, list)), noItem,
                Expression.Block(Expression.Assign(tuple, Expression.Call(items, select)),
                    Expression.Condition(Expression.ReferenceEqual(tuple, Expression.Constant(null, tupleType)), noItem,
                        Expression.Call(Expression.Property(tuple, "Item1"), getItem))))), panel).Compile();
        // Expression.Call keeps virtual Cancel dispatch, including subclass UI reset.
        _cancel = Expression.Lambda<Action<object>>(Expression.Call(Expression.Convert(panel, panelBase), cancel), panel).Compile();
        string[] panels = { "EnchantUI", "AugmentUI", "DisenchantUI", "RuneUI" };
        string[] workflows = { "EnchantItemAndReturnSuccessDialog", "AugmentItem", "DisenchantItem" };
        List<MethodInfo> actions = new();
        for (int index = 0; index < panels.Length; index++)
        {
            Type type = BridgeContracts.RequireType(epic, "EpicLoot_UnityLib." + panels[index]);
            if (!panelBase.IsAssignableFrom(type)) throw new InvalidOperationException("Unknown enchanting panel base.");
            MethodInfo action = BridgeContracts.RequireMethod(type, "DoMainAction", typeof(void));
            BridgeContracts.RequireStatic(action, false);
            if (index < workflows.Length)
                BridgeContracts.RequireCalls(action, "EpicLoot.CraftingV2.EnchantingUIController", workflows[index]);
            else
            {
                _rune = type;
                BridgeContracts.RequireCalls(action, type.FullName!, "ExtractSelectedEnchantment");
                BridgeContracts.RequireCalls(action, type.FullName!, "EtchSelectedRune");
                BridgeContracts.RequireCalls(BridgeContracts.RequireMethod(type, "ExtractSelectedEnchantment", typeof(bool),
                    typeof(ItemDrop.ItemData), typeof(float), typeof(float)), "EpicLoot_UnityLib.InventoryManagement", "RemoveExactItem");
            }
            actions.Add(action);
        }
        Type controller = BridgeContracts.RequireType(epic, "EpicLoot.CraftingV2.EnchantingUIController");
        MethodInfo augment = BridgeContracts.RequireMethod(controller, "OnAugmentComplete", typeof(void),
            typeof(ItemDrop.ItemData), typeof(int), BridgeContracts.RequireType(epic, "EpicLoot.MagicItemEffect"));
        BridgeContracts.RequireStatic(augment, true);
        BridgeContracts.RequireCalls(augment, "EpicLoot.MagicItem", "ReplaceEffect");
        actions.Add(augment);
        _actions = actions.ToArray();
    }

    private static void ValidateProvider(Assembly crafty)
    {
        const string provider = "AzuCraftyBoxes.Compatibility.EpicLootCompat";
        const string wrapper = "AzuCraftyBoxes.IContainers.IContainer";
        Type compat = BridgeContracts.RequireType(crafty, provider);
        BridgeContracts.RequireCalls(ContainerBridge.GetItemsMethod, provider, "Nearby");
        BridgeContracts.RequireCalls(ContainerBridge.GetItemsMethod, wrapper, "GetInventory");
        BridgeContracts.RequireCalls(ContainerBridge.GetItemsMethod, "Inventory", "GetAllItems");
        MethodInfo remove = BridgeContracts.RequireMethod(compat, "RemoveExactItem", typeof(int), typeof(ItemDrop.ItemData), typeof(int));
        BridgeContracts.RequireStatic(remove, true);
        BridgeContracts.RequireCalls(remove, "Inventory", "RemoveItem");
        BridgeContracts.RequireCalls(remove, wrapper, "Save");
        Type vanilla = BridgeContracts.RequireType(crafty, "AzuCraftyBoxes.IContainers.VanillaContainer");
        MethodInfo wrapperSave = BridgeContracts.RequireMethod(vanilla, "Save", typeof(void));
        MethodInfo wrapperInventory = BridgeContracts.RequireMethod(vanilla, "GetInventory", typeof(Inventory));
        BridgeContracts.RequireCalls(wrapperSave, "Container", "Save");
        BridgeContracts.RequireCalls(wrapperInventory, "Container", "GetInventory");
        Type wrapperType = BridgeContracts.RequireType(crafty, wrapper);
        _providerContracts = new[] { remove, wrapperSave, wrapperInventory,
            BridgeContracts.RequireMethod(compat, "Nearby", typeof(List<>).MakeGenericType(wrapperType)),
            BridgeContracts.RequireMethod(BridgeContracts.RequireType(crafty, "AzuCraftyBoxes.Util.Functions.MiscFunctions"),
                "HasAccessToContainer", typeof(bool), typeof(Container)) };
    }

    private static bool HasCompleteCoverage()
    {
        if (_harmony == null || _coverageFailed || HasCompetingMutators()) return false;
        for (int i = 0; i < _actions.Length; i++)
        {
            Patches? patches = Harmony.GetPatchInfo(_actions[i]);
            string prefix = i == _actions.Length - 1 ? nameof(BeforeAugment) : nameof(BeforePanel);
            if (patches == null || !patches.Prefixes.Any(p => Owns(p, prefix)) ||
                !patches.Finalizers.Any(p => Owns(p, nameof(AfterAction)))) return false;
        }
        string[] postfixes = { nameof(AfterProviderItems), nameof(AfterLoad), nameof(AfterSave), nameof(AfterStop) };
        for (int i = 0; i < _observed.Length; i++)
        {
            Patches? patches = Harmony.GetPatchInfo(_observed[i]);
            if (patches == null || !patches.Postfixes.Any(p => Owns(p, postfixes[i]))) return false;
        }
        return _actions.Length == 5 && _observed.Length == 4;
    }

    private static bool HasCompetingMutators()
    {
        // A foreign prefix can consume materials before this guard, or skip Save/Load;
        // a transpiler can invalidate the original-IL contracts. Do not claim protection
        // for those unknown workflows. Postfix observers retain their normal ordering.
        foreach (MethodInfo method in _actions.Concat(_observed.Take(3)).Concat(_providerContracts))
        {
            Patches? patches = Harmony.GetPatchInfo(method);
            if (patches == null) continue;
            Patch? competing = patches.Prefixes.Concat(patches.Transpilers)
                .FirstOrDefault(p => p.owner != _harmony!.Id);
            if (competing == null) continue;
            InteropFixesPlugin.ModLogger.LogWarning("EpicLoot/CraftyBoxes: unsupported competing mutation hook on " +
                method.DeclaringType?.FullName + "." + method.Name + " (owner " + competing.owner + "). Use player inventory for enchanting.");
            return true;
        }
        return false;
    }

    private static bool Owns(Patch patch, string method) => patch.owner == _harmony!.Id &&
        patch.PatchMethod.DeclaringType == typeof(EpicLootCraftyBoxesModule) && patch.PatchMethod.Name == method;

    private static HarmonyMethod Hook(string name) => new(typeof(EpicLootCraftyBoxesModule), name) { priority = Priority.Last };

    private static void CheckCoverage()
    {
        // UI operation boundary only; no reflection/IL/patch scans in Update or per container.
        if (HasCompleteCoverage()) return;
        _coverageFailed = true;
        ContainerBridge.TrackingFailed = true;
    }

    private static bool BeforePanel(object __instance, bool __runOriginal, out ContainerBridge.Operation? __state)
    {
        __state = null;
        if (!_active || !__runOriginal) return true;
        try
        {
            CheckCoverage();
            if (ContainerBridge.TryBegin(_selectedItem(__instance), false, out __state, _rune!.IsInstanceOfType(__instance))) return true;
        }
        catch (Exception error) { ContainerBridge.Report("The selected item could not be checked. Move it to your player inventory.", error); }
        try { _cancel(__instance); }
        catch (Exception error) { InteropFixesPlugin.ModLogger.LogWarning("Could not reset the enchanting panel: " + error); }
        return false;
    }

    private static bool BeforeAugment(ItemDrop.ItemData item, bool __runOriginal, out ContainerBridge.Operation? __state)
    {
        __state = null;
        if (!_active || !__runOriginal) return true;
        try { CheckCoverage(); return ContainerBridge.TryBegin(item, true, out __state); }
        catch (Exception error)
        {
            ContainerBridge.Report("Augment completion could not be checked. The previous effect was kept; the already-paid cost is not refunded.", error);
            return false;
        }
    }

    private static Exception? AfterAction(Exception? __exception, ContainerBridge.Operation? __state)
    {
        if (_active && __state != null)
        {
            if (__exception == null) ContainerBridge.Finish(__state);
            else ContainerBridge.Report("An enchanting action failed. Its final state was not saved by the bridge; materials may already have been consumed.", __exception);
        }
        return __exception;
    }

    private static void AfterProviderItems(List<ItemDrop.ItemData> __result)
    {
        if (!_active) return;
        try { ContainerBridge.Observe(__result); }
        catch (Exception error)
        {
            ContainerBridge.TrackingFailed = true;
            ContainerBridge.Report("Could not track CraftyBoxes items. Use your player inventory until the next connection.", error);
        }
    }

    private static void AfterLoad(Container __instance, bool __result) { if (_active && __result) ContainerBridge.Record(__instance, loaded: true); }
    private static void AfterSave(Container __instance) { if (_active) ContainerBridge.Record(__instance); }
    private static void AfterStop() { if (_active) ContainerBridge.Clear(); }

    internal static void Stop()
    {
        // Make any surviving hooks inert before cleanup, including a failed unpatch.
        _active = false;
        if (_harmony != null && ModuleIsolation.TryUnpatch(_harmony, "EpicLoot/CraftyBoxes")) _harmony = null;
        ContainerBridge.Reset();
        _actions = Array.Empty<MethodInfo>();
        _observed = Array.Empty<MethodInfo>();
        _providerContracts = Array.Empty<MethodInfo>();
        _selectedItem = null!;
        _cancel = null!;
        _rune = null;
        _coverageFailed = false;
    }
}
