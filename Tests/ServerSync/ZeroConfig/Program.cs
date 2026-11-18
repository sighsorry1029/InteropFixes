// Historical mandatory-ServerSync runtime fixture. Not compatible with the current
// optional-install DLL; see Tests/OptionalNetwork for the current runtime checks.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

internal static class Program
{
    internal const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    [DllImport("mono-2.0-sgen.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_add_internal_call(string name, IntPtr callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OffsetCall();
    private static readonly OffsetCall Offset = () => 0;

    private static int Main(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("final InteropFixes.dll, original Managed, BepInEx core required");
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string root in new[] { args[1], args[2], AppDomain.CurrentDomain.BaseDirectory })
                if (File.Exists(Path.Combine(root, name))) return Assembly.LoadFrom(Path.Combine(root, name));
            return null;
        };
        string sandbox = Path.Combine(Path.GetTempPath(), "InteropZeroConfig", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        typeof(Paths).GetMethod("SetExecutablePath", F)!.Invoke(null,
            new object[] { Path.Combine(sandbox, "valheim.exe"), Path.Combine(sandbox, "BepInEx"), args[1], new[] { args[1], args[2] } });
        InteropTests.ServerSyncHost.Prepare(typeof(Paths).Assembly);
        mono_add_internal_call("UnityEngine.Object::GetOffsetOfInstanceIDInCPlusPlusObject", Marshal.GetFunctionPointerForDelegate(Offset));
        try { return (int)typeof(Program).Assembly.GetType("Checks", true)!.GetMethod("Run", F)!.Invoke(null, new object[] { args[0], sandbox })!; }
        catch (Exception e) { System.Console.Error.WriteLine(e); return 1; }
        finally { typeof(Paths).Assembly.GetType("BepInEx.ThreadingHelper", true)!.GetProperty("Instance", F)!.SetValue(null, null); }
    }
}

internal static class Checks
{
    private const BindingFlags F = Program.F;
    private static Type Plugin = null!, SyncType = null!, Coordinator = null!;
    private static object Sync = null!;
    private static int checks, notifications;
    private static bool notificationWasReady;
    private static readonly List<IntPtr> Tokens = new();
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    private static object? Field(Type type, string name, object? owner = null) => AccessTools.Field(type, name).GetValue(owner);
    private static void Set(Type type, string name, object? value, object? owner = null) => AccessTools.Field(type, name).SetValue(owner, value);
    private static bool Property(Type type, string name, object? owner = null) => (bool)type.GetProperty(name, F)!.GetValue(owner)!;
    private static object Raw(Type type) => FormatterServices.GetUninitializedObject(type);
    private static object Alive(Type type)
    {
        object value = Raw(type);
        IntPtr token = Marshal.AllocHGlobal(4); Marshal.WriteInt32(token, Tokens.Count + 1); Tokens.Add(token);
        Set(typeof(UnityEngine.Object), "m_CachedPtr", token, value);
        return value;
    }
    private static bool Log() => false;
    private static bool SkipOriginal() => false;
    private static bool ObserveReadiness()
    {
        notifications++;
        notificationWasReady = Property(SyncType, "InitialSyncDone", Sync) && !Property(SyncType, "IsSourceOfTruth", Sync) &&
            Property(Plugin, "CosmeticSessionReady");
        return false; // The serialized multi-endpoint coordinator suite covers the actual report/grant exchange.
    }

    internal static int Run(string dll, string sandbox)
    {
        Assembly mod = Assembly.LoadFrom(dll);
        Plugin = mod.GetType("InteropFixes.InteropFixesPlugin", true)!;
        Coordinator = mod.GetType("InteropFixes.CosmeticNetworkCoordinator", true)!;
        Sync = Field(Plugin, "SyncedConfig")!; SyncType = Sync.GetType();
        var substitutes = new Harmony("InteropTests.ZeroConfig.Substitutes");
        var skip = new Harmony("InteropTests.ZeroConfig.Skip");
        object plugin = Alive(Plugin);
        MethodInfo awake = Plugin.GetMethod("Awake", F)!, destroy = Plugin.GetMethod("OnDestroy", F)!;
        MethodInfo receive = SyncType.GetMethod("RPC_FromServerConfigSync", F)!;
        try
        {
            foreach (string name in new[] { "Log", "LogWarning", "LogError" })
                substitutes.Patch(AccessTools.DeclaredMethod(typeof(Debug), name, new[] { typeof(object) }), prefix: new HarmonyMethod(typeof(Checks), nameof(Log)));
            substitutes.Patch(Coordinator.GetMethod("RefreshReadiness", F), prefix: new HarmonyMethod(typeof(Checks), nameof(ObserveReadiness)));
            Set(typeof(BaseUnityPlugin), "<Logger>k__BackingField", BepInEx.Logging.Logger.CreateLogSource("InteropZeroConfig"), plugin);
            string cfgPath = Path.Combine(sandbox, "sighsorry.InteropFixes.cfg");
            File.WriteAllText(cfgPath, "[MagicPlugin]\nEnableNetworkCosmeticOptimization = false\n");
            var config = new ConfigFile(cfgPath, false);
            Set(typeof(BaseUnityPlugin), "<Config>k__BackingField", config, plugin);
            awake.Invoke(plugin, null);
            Check(config.Count == 0, "actual Awake leaves stale configuration unbound");
            Check(!config.ContainsKey(new ConfigDefinition("MagicPlugin", "EnableNetworkCosmeticOptimization")), "old false value is not rebound");
            Check(Property(Plugin, "CosmeticRuntimeActive"), "old false value does not disable automatic runtime eligibility");
            Check(((IEnumerable)Field(SyncType, "allConfigs", Sync)!).Cast<object>().Count() == 0, "embedded ServerSync has zero registered settings");
            Check(SyncType.Assembly == mod && (bool)Field(SyncType, "ModRequired", Sync)!, "final embedded synchronization still requires the mod");
            Check((string)Field(SyncType, "CurrentVersion", Sync)! == "1.0.3" && (string)Field(SyncType, "MinimumRequiredVersion", Sync)! == "1.0.3", "current and minimum versions stay unchanged");
            Check(Harmony.GetPatchInfo(receive).Postfixes.Any(p => p.owner == "sighsorry.InteropFixes.CosmeticNetwork"), "real Awake installs initial synchronization completion hook");

            ZNet net = (ZNet)Alive(typeof(ZNet));
            Set(typeof(ZNet), "m_instance", net); Set(typeof(ZNet), "m_isServer", false);
            Set(Coordinator, "_net", net);
            ZRpc rpc = (ZRpc)Raw(typeof(ZRpc));
            ZNetPeer peer = (ZNetPeer)Raw(typeof(ZNetPeer)); peer.m_rpc = rpc; peer.m_server = true;
            Type connectionType = Coordinator.GetNestedType("Connection", F)!;
            object connection = Activator.CreateInstance(connectionType, F, null, new object[] { peer }, null)!;
            IDictionary connections = (IDictionary)Field(Coordinator, "Connections")!;
            connections.Add(rpc, connection);
            Check(!Property(Plugin, "CosmeticSessionReady"), "a client without initial ServerSync completion stays unready");
            notifications = 0;

            ZPackage message = Message(SyncType, false);
            Check(message.ReadByte() == 0 && message.ReadInt() == 2, "zero-settings package still carries internal version and administrator metadata");
            message.SetPos(0);
            receive.Invoke(Sync, new object[] { rpc, message });
            Check(Property(SyncType, "InitialSyncDone", Sync) && !Property(SyncType, "IsSourceOfTruth", Sync), "actual zero-settings RPC completes initial sync");
            Check(notifications == 1 && notificationWasReady, "notification occurs after initial readiness is committed");
            Check(Property(Plugin, "CosmeticSessionReady"), "automatic eligibility sees completed initial synchronization");

            notifications = 0;
            receive.Invoke(Sync, new object[] { rpc, Message(SyncType, true) });
            Check(config.Count == 0 && notifications == 1 && Property(Plugin, "CosmeticRuntimeActive"), "unknown old network setting cannot recreate or disable the removed option");
            notifications = 0;
            object unrelated = Activator.CreateInstance(SyncType, F, null, new object[] { "tests.unrelated" }, null)!;
            receive.Invoke(unrelated, new object[] { rpc, Message(SyncType, false) });
            Check(notifications == 0, "another ConfigSync instance cannot notify this coordinator");
            receive.Invoke(Sync, new object[] { Raw(typeof(ZRpc)), Message(SyncType, false) });
            Check(notifications == 0, "unregistered RPC cannot notify the coordinator");
            peer.m_server = false;
            receive.Invoke(Sync, new object[] { rpc, Message(SyncType, false) });
            Check(notifications == 0, "non-server connection cannot notify client readiness");
            peer.m_server = true;
            Set(typeof(ZNet), "m_instance", Alive(typeof(ZNet)));
            receive.Invoke(Sync, new object[] { rpc, Message(SyncType, false) });
            Check(notifications == 0, "stale network instance cannot notify client readiness");
            Set(typeof(ZNet), "m_instance", net);
            skip.Patch(receive, prefix: new HarmonyMethod(typeof(Checks), nameof(SkipOriginal)));
            receive.Invoke(Sync, new object[] { rpc, Message(SyncType, false) });
            Check(notifications == 0, "a skipped original synchronization does not notify readiness");
            skip.UnpatchSelf();
            Set(Plugin, "_stopping", true, plugin);
            receive.Invoke(Sync, new object[] { rpc, Message(SyncType, false) });
            Check(notifications == 0 && !Property(Plugin, "CosmeticRuntimeActive"), "shutdown blocks new readiness even with a valid old sync");
            Set(Plugin, "_stopping", false, plugin);
            connections.Clear(); // No simulated transport is live to unregister.
            destroy.Invoke(plugin, null);
            Check(Harmony.GetPatchInfo(receive)?.Postfixes.All(p => p.owner != "sighsorry.InteropFixes.CosmeticNetwork") != false, "teardown removes only its initial-sync hook");
            Check(!Property(Plugin, "CosmeticRuntimeActive"), "teardown disables automatic runtime eligibility");
            System.Console.WriteLine($"PASS: {checks} final merged zero-config synchronization checks. Real serialization, RPC parsing and Harmony completion hook; network transport and Unity lifecycle were not run.");
            return 0;
        }
        finally
        {
            ((IDictionary)Field(Coordinator, "Connections")!).Clear();
            destroy.Invoke(plugin, null);
            skip.UnpatchSelf(); substitutes.UnpatchSelf();
            Set(typeof(ZNet), "m_instance", null); Set(typeof(ZNet), "m_isServer", true);
            foreach (IntPtr token in Tokens) Marshal.FreeHGlobal(token);
        }
    }

    private static ZPackage Message(Type type, bool legacyOption)
    {
        Type entryType = type.GetNestedType("PackageEntry", F)!;
        Array entries = Array.CreateInstance(entryType, legacyOption ? 3 : 2);
        void Entry(int index, string section, string key, Type valueType, object value)
        {
            object entry = Activator.CreateInstance(entryType, true)!;
            Set(entryType, "section", section, entry); Set(entryType, "key", key, entry);
            Set(entryType, "type", valueType, entry); Set(entryType, "value", value, entry);
            entries.SetValue(entry, index);
        }
        Entry(0, "Internal", "serverversion", typeof(string), "1.0.3");
        Entry(1, "Internal", "lockexempt", typeof(bool), false);
        if (legacyOption) Entry(2, "MagicPlugin", "EnableNetworkCosmeticOptimization", typeof(bool), false);
        ZPackage result = (ZPackage)type.GetMethod("ConfigsToPackage", F)!.Invoke(null, new object?[] { null, null, entries, false })!;
        result.SetPos(0);
        return result;
    }
}
