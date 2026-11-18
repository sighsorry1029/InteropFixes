// Only Unity, game and VNEI boundaries are simulated. Lookup and IL rewrite code
// are compiled directly from the production sources by this test project.
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace UnityEngine
{
    public static class Application { public static bool isBatchMode; }
    public static class SystemInfo { public static Rendering.GraphicsDeviceType graphicsDeviceType = Rendering.GraphicsDeviceType.Direct3D11; }
    public class Object
    {
        public string name = "";
    }
    public class GameObject : Object { public bool activeInHierarchy = true; }
    public class Transform : Object { }
    public sealed class RectTransform : Transform { public bool ContainsPointer = true; }
    public class Camera : Object { }
    public enum RenderMode { ScreenSpaceOverlay, ScreenSpaceCamera }
    public class Canvas : Object { public RenderMode renderMode; public Camera? worldCamera; }
    public struct Vector2 { public float x, y; }
    public static class RectTransformUtility
    {
        public static Camera? LastCamera;
        public static bool RectangleContainsScreenPoint(RectTransform rect, Vector2 point, Camera? camera)
        { LastCamera = camera; return rect.ContainsPointer; }
    }
}
public class UITooltip
{
    internal static UITooltip? m_current;
    public static void SetCurrent(UITooltip? current) => m_current = current;
    public bool isActiveAndEnabled = true;
    public UnityEngine.Transform transform = new UnityEngine.RectTransform();
    public UnityEngine.Canvas? Canvas;
    public UnityEngine.GameObject? m_gamepadFocusObject;
    public string m_topic = "", m_text = "";
    public T? GetComponentInParent<T>() where T : class => Canvas as T;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Set(string topic, string text, UnityEngine.RectTransform anchor, UnityEngine.Vector2 position) { m_topic = topic; m_text = text; }
}
public struct Vector2i { public int x, y; public Vector2i(int x, int y) { this.x = x; this.y = y; } }
public class ItemDrop
{
    public class ItemData
    {
        public class SharedData { public string m_name = ""; }
        public SharedData m_shared = new();
        public UnityEngine.GameObject? m_dropPrefab;
        public Vector2i m_gridPos;
    }
}
public class Inventory
{
    public readonly Dictionary<(int, int), ItemDrop.ItemData> Items = new();
    public ItemDrop.ItemData? GetItemAt(int x, int y) => Items.TryGetValue((x, y), out var item) ? item : null;
}
public class InventoryGrid
{
    public bool isActiveAndEnabled = true;
    public Inventory Inventory = new();
    public ItemDrop.ItemData? Selected;
    public Inventory GetInventory() => Inventory;
    public ItemDrop.ItemData? GetGamepadSelectedItem() => Selected;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CreateItemTooltip(ItemDrop.ItemData item, UITooltip tooltip) { tooltip.m_topic = item.m_shared.m_name; }
}
public static class ZInput
{
    public static bool Mouse = true, Gamepad, Touch;
    public static bool IsMouseActive() => Mouse;
    public static bool IsExclusiveGamepadActive() => Gamepad;
    public static bool IsTouchActive() => Touch;
    public static UnityEngine.Vector2 pointerPosition => default;
}
namespace VNEI
{
    public class Plugin
    {
        public static object? viewRecipeHotkey;
        public static System.Text.RegularExpressions.Regex RichTagRegex = new("<[^>]*>");
        public static VNEI.Logic.Item? Shown;
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Update()
        {
            System.GC.KeepAlive(viewRecipeHotkey);
            if (UITooltip.m_current == null) return;
            string topic = UITooltip.m_current.m_topic.Trim();
            string text = UITooltip.m_current.m_text.Trim();
            VNEI.Logic.Item? item = null;
            if (topic.Length > 0) item = VNEI.Logic.Indexing.GetItem(topic);
            if (item == null && text.Length > 0) item = VNEI.Logic.Indexing.GetItem(text);
            if (item == null && RichTagRegex.IsMatch(topic))
            {
                string stripped = RichTagRegex.Replace(topic, string.Empty).Trim();
                item = VNEI.Logic.Indexing.GetItem(stripped);
            }
            if (item != null) new VNEI.UI.BaseUI().ShowRecipe(item, true);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RunUpdate() => Update();
    }
}
namespace VNEI.UI { public class BaseUI { public void ShowRecipe(VNEI.Logic.Item item, bool history) { VNEI.Plugin.Shown = item; } } }
namespace VNEI.Logic
{
    public class Item { public string Name; public Item(string name) { Name = name; } }
    public static class Indexing
    {
        public static readonly Dictionary<string, Item> Items = new();
        public static Item? GetItem(string name) => Items.TryGetValue(name, out var item) ? item : null;
    }
}
namespace UnityEngine.Rendering { public enum GraphicsDeviceType { Null, Direct3D11 } }
namespace BepInEx.Bootstrap
{
    public sealed class PluginInfo { public object? Instance; }
    public static class Chainloader { public static readonly Dictionary<string, PluginInfo> PluginInfos = new(); }
}
namespace InteropFixes
{
    internal static class InteropFixesPlugin { internal static readonly StubLogger ModLogger = new(); }
    internal sealed class StubLogger
    {
        internal readonly List<string> Messages = new();
        public void LogInfo(object message) => Messages.Add(message.ToString()!);
        public void LogWarning(object message) => Messages.Add(message.ToString()!);
    }
}
