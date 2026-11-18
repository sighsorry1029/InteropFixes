using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Linq.Expressions;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Item = ItemDrop.ItemData;

namespace InteropFixes.Modules.EpicLootCraftyBoxes;

internal static class ContainerBridge
{
    internal sealed class Operation
    {
        internal Item Item = null!;
        internal Container Container = null!;
        internal Inventory Inventory = null!;
        internal ZDO Zdo = null!;
        internal ushort OwnerRevision;
        internal long LoadSerial;
        internal long SaveSerial;
        internal bool AllowRemoval;
    }

    private sealed class Source
    {
        internal Container? Container;
        internal Inventory? Inventory;
    }

    private sealed class Snapshot
    {
        internal Inventory Inventory = null!;
        internal byte[]? Items;
        internal bool Known;
        internal long LoadSerial;
        internal long SaveSerial;
    }

    private static ConditionalWeakTable<Item, Source> _sources = new();
    private static ConditionalWeakTable<Container, Snapshot> _snapshots = new();
    private static Func<IList> _nearby = null!;
    private static Func<object, Inventory> _getInventory = null!;
    private static Type _vanillaContainer = null!;
    private static Func<List<Item>> _getItems = null!;
    private static Func<Container, bool> _hasAccess = null!;
    private static FieldInfo _wrappedContainer = null!;
    private static ConfigEntry<float> _range = null!;
    private static AccessTools.FieldRef<Container, ZNetView> _view = null!;
    private static AccessTools.FieldRef<Container, bool> _loading = null!;
    private static AccessTools.FieldRef<Container, uint> _loadedRevision = null!;
    private static Action<Container> _save = null!;
    internal static MethodInfo GetItemsMethod = null!;
    internal static bool TrackingFailed;
    private static float _lastNotice = float.NegativeInfinity;

    internal static void Initialize(Assembly crafty)
    {
        Clear();
        Type compat = BridgeContracts.RequireType(crafty, "AzuCraftyBoxes.Compatibility.EpicLootCompat");
        Type wrapperType = BridgeContracts.RequireType(crafty, "AzuCraftyBoxes.IContainers.IContainer");
        _vanillaContainer = BridgeContracts.RequireType(crafty, "AzuCraftyBoxes.IContainers.VanillaContainer");
        if (!wrapperType.IsInterface || !wrapperType.IsAssignableFrom(_vanillaContainer))
            throw new InvalidOperationException("Unknown CraftyBoxes container interface.");
        GetItemsMethod = BridgeContracts.RequireMethod(compat, "GetItems", typeof(List<Item>));
        BridgeContracts.RequireStatic(GetItemsMethod, true);
        _getItems = AccessTools.MethodDelegate<Func<List<Item>>>(GetItemsMethod);
        MethodInfo nearby = BridgeContracts.RequireMethod(compat, "Nearby", typeof(List<>).MakeGenericType(wrapperType));
        BridgeContracts.RequireStatic(nearby, true);
        _nearby = Expression.Lambda<Func<IList>>(Expression.Convert(Expression.Call(nearby), typeof(IList))).Compile();
        MethodInfo inventory = BridgeContracts.RequireMethod(wrapperType, "GetInventory", typeof(Inventory));
        BridgeContracts.RequireStatic(inventory, false);
        ParameterExpression wrapper = Expression.Parameter(typeof(object), "wrapper");
        _getInventory = Expression.Lambda<Func<object, Inventory>>(
            Expression.Call(Expression.Convert(wrapper, wrapperType), inventory), wrapper).Compile();
        Type misc = BridgeContracts.RequireType(crafty, "AzuCraftyBoxes.Util.Functions.MiscFunctions");
        MethodInfo access = BridgeContracts.RequireMethod(misc, "HasAccessToContainer", typeof(bool), typeof(Container));
        BridgeContracts.RequireStatic(access, true);
        _hasAccess = AccessTools.MethodDelegate<Func<Container, bool>>(
            access);
        // This is the actual metadata name of ACB 1.8.28's primary-constructor field.
        _wrappedContainer = BridgeContracts.RequireField(_vanillaContainer, "<_container>P", typeof(Container), false);
        FieldInfo rangeField = BridgeContracts.RequireField(
            BridgeContracts.RequireType(crafty, "AzuCraftyBoxes.AzuCraftyBoxesPlugin"), "mRange", typeof(ConfigEntry<float>), true);
        _range = rangeField.GetValue(null) as ConfigEntry<float>
            ?? throw new InvalidOperationException("CraftyBoxes range configuration is not ready.");
        BridgeContracts.RequireField(typeof(Container), "m_nview", typeof(ZNetView), false);
        BridgeContracts.RequireField(typeof(Container), "m_loading", typeof(bool), false);
        BridgeContracts.RequireField(typeof(Container), "m_lastRevision", typeof(uint), false);
        _view = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        _loading = AccessTools.FieldRefAccess<Container, bool>("m_loading");
        _loadedRevision = AccessTools.FieldRefAccess<Container, uint>("m_lastRevision");
        MethodInfo save = BridgeContracts.RequireMethod(typeof(Container), "Save", typeof(void));
        BridgeContracts.RequireStatic(save, false);
        _save = AccessTools.MethodDelegate<Action<Container>>(save);
    }

    internal static void Observe(List<Item> items)
    {
        if (items == null || items.Count == 0) return;
        // Reuse CraftyBoxes' own nearby/provider policy, not a global scene scan.
        Dictionary<Item, Source> current = new();
        foreach (object wrapper in _nearby())
        {
            Inventory? inventory = _getInventory(wrapper);
            if (inventory == null) continue;
            Container? container = _vanillaContainer.IsInstanceOfType(wrapper) ? _wrappedContainer.GetValue(wrapper) as Container : null;
            Source source = new() { Container = container, Inventory = inventory };
            if (container != null)
            {
                Snapshot snapshot = _snapshots.GetValue(container, static _ => new Snapshot());
                ZNetView view = _view(container);
                if (!snapshot.Known && view != null && view.IsValid() && !_loading(container) &&
                    _loadedRevision(container) == view.GetZDO().DataRevision)
                    Capture(container, snapshot);
            }
            foreach (Item item in inventory.GetAllItems()) current[item] = source;
        }
        foreach (Item item in items)
        {
            if (item == null) continue;
            // Keep stale references classified as CraftyBoxes items even after a reload.
            _sources.Remove(item);
            _sources.Add(item, current.TryGetValue(item, out Source? source) && source != null ? source : new Source());
        }
    }

    internal static bool TryBegin(Item? item, bool completingAugment, out Operation? operation, bool allowRemoval = false)
    {
        operation = null;
        if (item == null) return true;
        Player player = Player.m_localPlayer;
        if (player == null) return false;
        if (Contains(player.GetInventory(), item)) return true;
        if (TrackingFailed)
        {
            Report("Storage tracking failed. Move the equipment to your player inventory before enchanting.");
            return false;
        }

        bool wasCrafty = _sources.TryGetValue(item, out _);
        List<Item> available = _getItems(); // Recheck exclusions/pull mode; also refresh source tracking.
        // Do not rely on a delegate created before Harmony patching going through its postfix.
        Observe(available);
        if (TrackingFailed)
        {
            Report("Storage tracking failed. Move the equipment to your player inventory before enchanting.");
            return false;
        }
        bool isCrafty = Contains(available, item);
        if (!wasCrafty && !isCrafty) return true; // Other providers are outside this bridge.
        string reason;
        if (!isCrafty) reason = "The selected storage item is no longer available. Reopen the table and select it again.";
        else if (!_sources.TryGetValue(item, out Source? source) || source == null || source.Container == null || source.Inventory == null)
            reason = "This storage type is not supported. Move the equipment to your player inventory.";
        else if (!CanUse(source.Container, source.Inventory, item, out reason)) { }
        else
        {
            ZDO zdo = _view(source.Container).GetZDO();
            Snapshot snapshot = _snapshots.GetValue(source.Container, _ => throw new InvalidOperationException("Missing snapshot."));
            operation = new Operation
            {
                Item = item, Container = source.Container, Inventory = source.Inventory,
                Zdo = zdo, OwnerRevision = zdo.OwnerRevision,
                LoadSerial = snapshot.LoadSerial, SaveSerial = snapshot.SaveSerial, AllowRemoval = allowRemoval
            };
            return true;
        }
        if (completingAugment)
            reason += " The previous effect was kept; the already-paid augment cost is not refunded.";
        Report(reason);
        return false;
    }

    private static bool CanUse(Container container, Inventory inventory, Item item, out string reason)
    {
        reason = "The storage state changed. Close and reopen the container, then select the item again.";
        if (container == null) return false;
        if (container.GetType() != typeof(Container))
        {
            reason = "This container implementation is not supported. Move the equipment to your player inventory.";
            return false;
        }
        ZNetView view = _view(container);
        if (view == null || !view.IsValid() || _loading(container) ||
            !ReferenceEquals(container.GetInventory(), inventory) || !Contains(inventory, item)) return false;
        if (!view.IsOwner())
        {
            reason = "Open and close this container to acquire ownership, or move the equipment to your player inventory.";
            return false;
        }
        if (container.IsInUse() || view.GetZDO().GetInt(ZDOVars.s_inUse) != 0)
        {
            reason = "Close the container before enchanting its equipment.";
            return false;
        }
        if (!_hasAccess(container) || Vector3.Distance(Player.m_localPlayer.transform.position, container.transform.position) > _range.Value)
        {
            reason = "This container is out of range or access is denied.";
            return false;
        }
        if (!_snapshots.TryGetValue(container, out Snapshot? snapshot) || snapshot == null || !snapshot.Known ||
            !ReferenceEquals(snapshot.Inventory, inventory) || !SameBytes(snapshot.Items, view.GetZDO().GetByteArray(ZDOVars.s_items)))
            return false;
        return true;
    }

    internal static void Finish(Operation operation)
    {
        try
        {
            Container container = operation.Container;
            if (container == null) throw new InvalidOperationException("Container was destroyed.");
            ZNetView view = _view(container);
            if (view == null || !view.IsValid() || !ReferenceEquals(operation.Zdo, view.GetZDO()) ||
                !view.IsOwner() || view.GetZDO().OwnerRevision != operation.OwnerRevision || _loading(container) ||
                !ReferenceEquals(operation.Inventory, container.GetInventory()))
                throw new InvalidOperationException("Storage identity or ownership changed during the action.");
            if (!_snapshots.TryGetValue(container, out Snapshot? snapshot) || snapshot == null || !snapshot.Known ||
                snapshot.LoadSerial != operation.LoadSerial ||
                !SameBytes(snapshot.Items, view.GetZDO().GetByteArray(ZDOVars.s_items)))
                throw new InvalidOperationException("Storage was reloaded or updated during the action.");
            if (!Contains(operation.Inventory, operation.Item))
            {
                // Rune extraction may intentionally destroy the selected equipment. Its normal
                // RemoveExactItem path already saves. Never reinsert or value-match that item.
                if (operation.AllowRemoval && snapshot.SaveSerial > operation.SaveSerial) return;
                throw new InvalidOperationException("Selected item no longer belongs to its source inventory.");
            }
            if (!CanUse(container, operation.Inventory, operation.Item, out string reason))
                throw new InvalidOperationException(reason);
            // Do not load or restore an old inventory snapshot here. Save the final live item,
            // including durability changes made after EpicLoot's MagicItemChanged event.
            _save(container);
            Record(container); // Also cover cached delegates which bypass Harmony's Save postfix.
        }
        catch (Exception e)
        {
            Report("The enchanting result could not be safely saved. Do not repeat the action; check the client log. Materials may already have been consumed.", e);
        }
    }

    internal static void Record(Container container, bool loaded = false)
    {
        // Only observed CraftyBoxes containers incur a byte snapshot; no world-wide scan.
        if (!_snapshots.TryGetValue(container, out Snapshot? snapshot) || snapshot == null) return;
        try
        {
            if (loaded) snapshot.LoadSerial++;
            else snapshot.SaveSerial++;
            Capture(container, snapshot);
        }
        catch (Exception e)
        {
            snapshot.Known = false;
            InteropFixesPlugin.ModLogger.LogWarning("Could not record container persistence state: " + e);
        }
    }

    private static void Capture(Container container, Snapshot snapshot)
    {
        ZNetView view = _view(container);
        snapshot.Known = false;
        if (view == null || !view.IsValid() || _loading(container) ||
            _loadedRevision(container) != view.GetZDO().DataRevision) return;
        snapshot.Inventory = container.GetInventory();
        byte[]? bytes = view.GetZDO().GetByteArray(ZDOVars.s_items);
        snapshot.Items = bytes == null ? null : (byte[])bytes.Clone();
        snapshot.Known = bytes != null;
    }

    internal static bool SameBytes(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }

    private static bool Contains(Inventory inventory, Item item) => Contains(inventory.GetAllItems(), item);
    private static bool Contains(List<Item> items, Item item)
    {
        for (int i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item)) return true;
        return false;
    }

    internal static void Report(string message, Exception? error = null)
    {
        InteropFixesPlugin.ModLogger.LogWarning(message + (error == null ? "" : "\n" + error));
        if (Player.m_localPlayer != null && Time.realtimeSinceStartup - _lastNotice >= 2f)
        {
            _lastNotice = Time.realtimeSinceStartup;
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, "EpicLoot CraftyBoxes Bridge: " + message);
        }
    }

    internal static void Clear()
    {
        _sources = new ConditionalWeakTable<Item, Source>();
        _snapshots = new ConditionalWeakTable<Container, Snapshot>();
        TrackingFailed = false;
        _lastNotice = float.NegativeInfinity;
    }

    internal static void Reset()
    {
        Clear();
        _nearby = null!;
        _getInventory = null!;
        _getItems = null!;
        _hasAccess = null!;
        _wrappedContainer = null!;
        _vanillaContainer = null!;
        _range = null!;
        _view = null!;
        _loading = null!;
        _loadedRevision = null!;
        _save = null!;
        GetItemsMethod = null!;
    }
}

