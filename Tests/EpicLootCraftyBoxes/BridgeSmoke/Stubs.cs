using System.Reflection;
using System.Text.Json;
using InteropFixes.Modules.EpicLootCraftyBoxes;

// Narrow test doubles, not publicized game assemblies. These tests exercise the linked
// production guard/persistence code, not Unity, Harmony dispatch, or live network timing.
namespace UnityEngine
{
    public struct Vector3(float x, float y, float z)
    {
        public float x = x, y = y, z = z;
        public static float Distance(Vector3 a, Vector3 b) =>
            MathF.Sqrt(MathF.Pow(a.x - b.x, 2) + MathF.Pow(a.y - b.y, 2) + MathF.Pow(a.z - b.z, 2));
    }
    public sealed class Transform { public Vector3 position; }
    public static class Time { public static float realtimeSinceStartup; }
}

namespace BepInEx.Configuration
{
    public sealed class ConfigEntry<T>(T value) { public T Value = value; }
}

namespace HarmonyLib
{
    public static class AccessTools
    {
        public delegate ref F FieldRef<T, F>(T instance);
        public static FieldInfo DeclaredField(Type type, string name) =>
            type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)!;
        public static T MethodDelegate<T>(MethodInfo method) where T : Delegate => method.CreateDelegate<T>();
        public static FieldRef<T, F> FieldRefAccess<T, F>(string name)
        {
            object result = name switch
            {
                "m_nview" => (FieldRef<Container, ZNetView>)Container.ViewRef,
                "m_loading" => (FieldRef<Container, bool>)Container.LoadingRef,
                "m_lastRevision" => (FieldRef<Container, uint>)Container.RevisionRef,
                _ => throw new MissingFieldException(name)
            };
            return (FieldRef<T, F>)result;
        }
    }
}

namespace InteropFixes.Modules.EpicLootCraftyBoxes
{
    public static class InteropFixesPlugin
    {
        public static readonly TestLogger ModLogger = new();
        public static MethodInfo RequireMethod(Type type, string name, Type result, params Type[] args)
        {
            MethodInfo? method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly, null, args, null);
            return method?.ReturnType == result ? method : throw new MissingMethodException(type.FullName, name);
        }
    }
    public sealed class TestLogger
    {
        public readonly List<string> Warnings = [];
        public void LogWarning(string message) => Warnings.Add(message);
        public void LogInfo(string message) { }
    }
}

public sealed class ItemDrop
{
    public sealed class ItemData
    {
        public string Name { get; set; } = "ShieldWood";
        public string Magic { get; set; } = "plain";
        public float Durability { get; set; } = 5;
    }
}
public sealed class Inventory
{
    public readonly List<ItemDrop.ItemData> Items = [];
    public List<ItemDrop.ItemData> GetAllItems() => Items;
}
public sealed class ZDO
{
    public uint DataRevision = 1;
    public ushort OwnerRevision = 1;
    public byte[]? Items;
    public int InUse;
    public byte[]? GetByteArray(int key) => Items;
    public int GetInt(int key) => InUse;
}
public static class ZDOVars { public const int s_inUse = 1, s_items = 2; }
public sealed class ZNetView
{
    public bool Owner = true, Valid = true;
    public ZDO Zdo = new();
    public bool IsOwner() => Owner;
    public bool IsValid() => Valid;
    public ZDO GetZDO() => Zdo;
}
public sealed class Container
{
    private ZNetView m_nview = new();
    private bool m_loading;
    private uint m_lastRevision = 1;
    public Inventory Inventory = new();
    public readonly UnityEngine.Transform transform = new();
    public bool Busy, Access = true, ThrowOnSave, SkipSavePostfix;
    public int Saves, Loads;
    public ZNetView View => m_nview;
    public Inventory GetInventory() => Inventory;
    public bool IsInUse() => Busy;
    public static ref ZNetView ViewRef(Container value) => ref value.m_nview;
    public static ref bool LoadingRef(Container value) => ref value.m_loading;
    public static ref uint RevisionRef(Container value) => ref value.m_lastRevision;
    public void Seed() => SaveCore();
    public void SaveForMaterials() => Save();
    private void Save()
    {
        if (ThrowOnSave) throw new IOException("test save failure");
        Saves++;
        SaveCore();
        if (!SkipSavePostfix) ContainerBridge.Record(this); // Simulated successful Harmony Save postfix.
    }
    private void SaveCore()
    {
        m_nview.Zdo.Items = JsonSerializer.SerializeToUtf8Bytes(Inventory.Items);
        m_nview.Zdo.DataRevision++;
        m_lastRevision = m_nview.Zdo.DataRevision;
    }
    public void Reload()
    {
        Loads++;
        m_loading = true;
        Inventory.Items.Clear();
        Inventory.Items.AddRange(JsonSerializer.Deserialize<List<ItemDrop.ItemData>>(m_nview.Zdo.Items!)!);
        m_loading = false;
        m_lastRevision = m_nview.Zdo.DataRevision;
        ContainerBridge.Record(this, true); // Simulated successful Load postfix.
    }
    public void MetadataChanged() => m_nview.Zdo.DataRevision++;
}
public sealed class Player
{
    public static Player m_localPlayer = null!;
    public readonly UnityEngine.Transform transform = new();
    public readonly Inventory Inventory = new();
    public readonly List<string> Messages = [];
    public Inventory GetInventory() => Inventory;
    public void Message(MessageHud.MessageType type, string message) => Messages.Add(message);
}
public static class MessageHud { public enum MessageType { Center } }
public sealed class ZNet { private void StopAll(bool save) { } }

namespace AzuCraftyBoxes
{
    public static class AzuCraftyBoxesPlugin
    {
        public static BepInEx.Configuration.ConfigEntry<float> mRange = new(20);
    }
}
namespace AzuCraftyBoxes.IContainers
{
    public interface IContainer { Inventory GetInventory(); }
    public sealed class VanillaContainer(Container _container) : IContainer
    {
        public Inventory GetInventory() => _container.GetInventory();
    }
    public sealed class UnsupportedContainer(Inventory inventory) : IContainer
    {
        public Inventory GetInventory() => inventory;
    }
}
namespace AzuCraftyBoxes.Util.Functions
{
    public static class MiscFunctions
    {
        private static bool HasAccessToContainer(Container container) => container.Access;
    }
}
namespace AzuCraftyBoxes.Compatibility
{
    public static class EpicLootCompat
    {
        public static readonly List<IContainers.IContainer> Containers = [];
        public static List<ItemDrop.ItemData>? OverrideItems;
        public static bool SkipProviderPostfix;
        private static List<IContainers.IContainer> Nearby() => Containers;
        private static List<ItemDrop.ItemData> GetItems()
        {
            List<ItemDrop.ItemData> result = OverrideItems ?? Containers.SelectMany(c => c.GetInventory().GetAllItems()).ToList();
            if (!SkipProviderPostfix) ContainerBridge.Observe(result); // Simulated provider postfix.
            return result;
        }
        public static void ObserveNow() => GetItems();
    }
}

