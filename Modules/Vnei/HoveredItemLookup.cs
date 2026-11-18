using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace InteropFixes.Modules.Vnei;

internal static class HoveredItemLookup
{
    private sealed class Binding
    {
        internal InventoryGrid Grid = null!;
        internal Inventory Inventory = null!;
        internal ItemDrop.ItemData? Item;
        internal int X, Y;
    }

    private static ConditionalWeakTable<UITooltip, Binding> _bindings = new();
    private static readonly ConditionalWeakTable<UITooltip, Binding>.CreateValueCallback NewBinding = _ => new Binding();
    private static FieldInfo? _current;
    private static Func<string, object?>? _getItem;
    private static bool _enabled;

    internal static void Initialize(Func<string, object?> getItem)
    {
        _current = AccessTools.DeclaredField(typeof(UITooltip), "m_current");
        if (_current == null || !_current.IsStatic || _current.FieldType != typeof(UITooltip))
            throw new MissingFieldException(typeof(UITooltip).FullName, "m_current");
        _getItem = getItem;
    }

    internal static void Enable() => _enabled = true;

    internal static void Capture(InventoryGrid __instance, ItemDrop.ItemData item, UITooltip tooltip)
    {
        if (!_enabled || tooltip == null || __instance == null || item?.m_shared == null) return;
        Inventory inventory = __instance.GetInventory();
        if (inventory == null) return;
        Binding binding = _bindings.GetValue(tooltip, NewBinding);
        binding.Grid = __instance;
        binding.Inventory = inventory;
        binding.Item = item;
        binding.X = item.m_gridPos.x;
        binding.Y = item.m_gridPos.y;
    }

    internal static void Invalidate(UITooltip __instance)
    {
        if (_enabled && __instance != null && _bindings.TryGetValue(__instance, out Binding? binding)) binding.Item = null;
    }

    internal static object? Resolve(string name)
    {
        // Installed resolver always has a delegate, including during partial teardown.
        Func<string, object?> getItem = _getItem ?? throw new InvalidOperationException("VNEI lookup adapter is not initialized.");
        var tooltip = _enabled ? _current?.GetValue(null) as UITooltip : null;
        if (tooltip != null && tooltip.isActiveAndEnabled && _bindings.TryGetValue(tooltip, out Binding? binding) && IsCurrent(tooltip, binding))
        {
            ItemDrop.ItemData item = binding.Item!;
            if (item.m_dropPrefab != null)
            {
                object? indexed = getItem(item.m_dropPrefab.name);
                if (indexed != null) return indexed;
            }
            if (!string.IsNullOrEmpty(item.m_shared.m_name))
            {
                object? indexed = getItem(item.m_shared.m_name);
                if (indexed != null) return indexed;
            }
        }
        return getItem(name);
    }

    private static bool IsCurrent(UITooltip tooltip, Binding binding)
    {
        ItemDrop.ItemData? item = binding.Item;
        if (binding.Grid == null || !binding.Grid.isActiveAndEnabled || item?.m_shared == null ||
            binding.Grid.GetInventory() != binding.Inventory ||
            item.m_gridPos.x != binding.X || item.m_gridPos.y != binding.Y ||
            !ReferenceEquals(binding.Inventory.GetItemAt(binding.X, binding.Y), item)) return false;
        if (!ZInput.IsMouseActive() && (ZInput.IsExclusiveGamepadActive() || ZInput.IsTouchActive()))
            return tooltip.m_gamepadFocusObject != null
                ? tooltip.m_gamepadFocusObject.activeInHierarchy
                : ReferenceEquals(binding.Grid.GetGamepadSelectedItem(), item);
        if (tooltip.transform is not RectTransform rect) return false;
        Canvas? canvas = tooltip.GetComponentInParent<Canvas>();
        Camera? camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        return RectTransformUtility.RectangleContainsScreenPoint(rect, ZInput.pointerPosition, camera);
    }

    internal static void Disable()
    {
        _enabled = false;
        _bindings = new ConditionalWeakTable<UITooltip, Binding>();
        _current = null;
    }

    internal static void Clear()
    {
        Disable();
        _getItem = null;
    }
}
