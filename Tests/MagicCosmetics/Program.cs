using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;

namespace InteropFixes
{
    internal static class InteropFixesPlugin
    {
        internal const string MagicPluginGuid = "blacks7ar.MagicPlugin";
        internal static readonly ManualLogSource ModLogger = new("Magic cosmetic isolated tests");
    }
    // The parent coordinator's own state/transport tests cover agreement. Here its
    // boundary is controlled so production receiver vs emission policy is testable.
    internal static class CosmeticNetworkCoordinator
    {
        internal static int Allowed;
        internal static int Reported;
        internal static long SessionToken => 12345;
        internal static bool CanOptimize(int feature) => (Allowed & feature) == feature;
        internal static void SetLocalCapabilities(int mask) => Reported = mask;
        internal static bool IsParticipant(long sender) => sender == 54321;
    }
}

internal static class Program
{
    private static readonly Type Module = typeof(InteropFixes.MagicPluginCosmeticZdoPrevention);
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static int _checks;
    private static MethodInfo Method(string name) => Module.GetMethod(name, Any)!;
    private static object? Invoke(string name, params object?[] args) => Method(name).Invoke(null, args);
    private static object Enum(string name, int value) => System.Enum.ToObject(Module.GetNestedType(name, BindingFlags.NonPublic)!, value);
    private static object Construct(string name, params object?[] args) => Activator.CreateInstance(Module.GetNestedType(name, BindingFlags.NonPublic)!, Any, null, args, null)!;
    private static void Set(string name, object? value) => Module.GetField(name, Any)!.SetValue(null, value);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); _checks++; }

    private static void Main(string[] args)
    {
        Packets();
        History();
        Registration();
        PermissionBoundary();
        Cleanup();
        CompiledBoundaries(args.Length == 0 ? Assembly.GetExecutingAssembly().Location : args[0]);
        System.Console.WriteLine($"PASS: {_checks} cosmetic assertions against original Valheim DLLs, real routed-RPC registration/Harmony hook, and production packet/history code. Unity rendering and live multiplayer were not run.");
    }

    private static void Packets()
    {
        ZDOID source = new(900, 7);
        Vector3 point = new(1, 2, 3);
        Quaternion rotation = Quaternion.identity;
        foreach (int feature in new[] { 4, 16, 32 })
        {
            object record = Construct("CosmeticEvent", 12345L, source, Enum("CosmeticFeature", feature), point, rotation);
            ZPackage encoded = (ZPackage)Invoke("EncodeCosmeticEvent", record)!;
            Check(encoded.Size() == 52, "Wire payload changed size");
            object?[] args = { new ZPackage(encoded.GetArray()), 12345L, null };
            Check((bool)Method("TryDecodeCosmeticEvent").Invoke(null, args)!, "Valid committed event rejected");
            object restored = args[2]!;
            Check(Equals(restored.GetType().GetProperty("Source", Any)!.GetValue(restored), source), "Source identity changed");
            args = new object?[] { new ZPackage(encoded.GetArray()), 999L, null };
            Check(!(bool)Method("TryDecodeCosmeticEvent").Invoke(null, args)!, "Old session event accepted");
            args = new object?[] { new ZPackage(encoded.GetArray()), 0L, null };
            Check(!(bool)Method("TryDecodeCosmeticEvent").Invoke(null, args)!, "Unnegotiated session accepted event");
        }
        bool Valid(int feature, ZDOID id, Vector3 pos, Quaternion rot) => (bool)Invoke("IsValidCosmeticEvent", feature, id, pos, rot)!;
        Check(!Valid(1, source, point, rotation), "Carrier-only feature accepted as an explicit event");
        Check(!Valid(20, source, point, rotation), "Composite event feature accepted");
        Check(!Valid(4, ZDOID.None, point, rotation), "Missing carrier identity accepted");
        Check(!Valid(4, source, new Vector3(float.NaN, 0, 0), rotation), "NaN position accepted");
        Check(!Valid(4, source, new Vector3(float.PositiveInfinity, 0, 0), rotation), "Infinite position accepted");
        Check(!Valid(4, source, new Vector3(1000001, 0, 0), rotation), "Unbounded position accepted");
        Check(!Valid(4, source, point, new Quaternion(0, 0, 0, 0)), "Zero quaternion accepted");
        Check(!Valid(4, source, point, new Quaternion(float.NaN, 0, 0, 1)), "NaN quaternion accepted");
        foreach (int size in new[] { 0, 1, 51, 53, 100 })
        {
            object?[] args = { new ZPackage(new byte[size]), 12345L, null };
            Check(!(bool)Method("TryDecodeCosmeticEvent").Invoke(null, args)!, "Invalid event packet length accepted");
        }
    }

    private static void History()
    {
        object history = Construct("CosmeticReplayHistory", 3);
        MethodInfo record = history.GetType().GetMethod("TryRecord", Any)!;
        bool Record(long session, uint id, int feature) => (bool)record.Invoke(history, new object[] { session, new ZDOID(900, id), feature })!;
        Check(Record(1, 1, 4), "First event rejected");
        Check(!Record(1, 1, 4), "Repeated event replayed");
        Check(Record(1, 1, 16), "Distinct feature rejected");
        Check(Record(1, 2, 4), "Distinct source rejected");
        Check(Record(1, 3, 4), "History stopped accepting at capacity");
        Check((int)history.GetType().GetProperty("Count", Any)!.GetValue(history)! == 3, "History exceeded its bound");
        Check(Record(2, 3, 4), "New session retained old dedupe state");
        Check((int)history.GetType().GetProperty("Count", Any)!.GetValue(history)! == 1, "New session did not release old history");
        history.GetType().GetMethod("Clear", Any)!.Invoke(history, null);
        Check(Record(2, 3, 4), "Scene cleanup did not release dedupe state");
    }

    private static void Registration()
    {
        FieldInfo map = typeof(ZRoutedRpc).GetField("m_functions", Any)!;
        IDictionary access(ZRoutedRpc rpc) => (IDictionary)map.GetValue(rpc)!;
        Check(!typeof(ZRoutedRpc).GetField("m_functions", Any)!.IsPublic, "Expected original private RPC map");
        Set("_rpcFunctions", map);
        Set("_cosmeticZdoPatchInstalled", true);
        Set("_cosmeticFeatures", Enum("CosmeticFeature", 127));
        Harmony harmony = new("InteropFixes.Tests.CosmeticConstructor");
        try
        {
            harmony.Patch(AccessTools.Constructor(typeof(ZRoutedRpc), new[] { typeof(bool) }),
                postfix: new HarmonyMethod(Method("RoutedRpcCreatedPostfix")));
            ZRoutedRpc first = new(false);
            Check(access(first).Count == 1, "Constructor did not prepare receiver");
            Check(InteropFixes.CosmeticNetworkCoordinator.Reported == 127, "Prepared receiver was not included in capabilities");
            Invoke("AttachEventReceiver", first);
            Check(access(first).Count == 1, "Repeated attachment duplicated RPC");
            first.Register("test.unrelated", _ => { });
            ZRoutedRpc second = new(false);
            Check(access(first).Count == 1, "Session replacement removed foreign RPC or kept old own RPC");
            Check(access(second).Count == 1, "Session replacement did not attach receiver");
            string name = (string)Module.GetField("CosmeticEventRpc", Any)!.GetRawConstantValue();
            int hash = access(second).Keys.Cast<int>().Single();
            access(second).Remove(hash);
            second.Register<ZPackage>(name, (_, _) => { });
            object foreign = access(second)[hash]!;
            Invoke("DetachEventReceiver");
            Check(ReferenceEquals(access(second)[hash], foreign), "Detach removed a foreign replacement");
            Invoke("AttachEventReceiver", second);
            Check(ReferenceEquals(access(second)[hash], foreign), "Collision overwrote foreign RPC");
            Check(InteropFixes.CosmeticNetworkCoordinator.Reported == 75, "Unavailable receiver did not withhold event-only capabilities");
        }
        finally { Invoke("DetachEventReceiver"); harmony.UnpatchSelf(); }
    }

    private static void PermissionBoundary()
    {
        // Both scene references are null in this isolated host; the prepared local
        // feature mask is controlled directly without constructing a Unity object.
        Set("_probedCosmeticScene", null);
        Set("_cosmeticZdoPatchInstalled", true);
        Set("_cosmeticFeatures", Enum("CosmeticFeature", 127));
        InteropFixes.CosmeticNetworkCoordinator.Allowed = 0;
        Check((bool)Invoke("HasCosmeticFeature", Enum("CosmeticFeature", 4))!, "Receiver readiness depended on the OFF policy");
        Check(!(bool)Invoke("CanOptimizeFeature", Enum("CosmeticFeature", 4))!, "New suppression ignored OFF/unknown agreement");
        InteropFixes.CosmeticNetworkCoordinator.Allowed = 4;
        Check((bool)Invoke("CanOptimizeFeature", Enum("CosmeticFeature", 4))!, "Agreed feature stayed disabled");
        Check(!(bool)Invoke("CanOptimizeFeature", Enum("CosmeticFeature", 16))!, "Unsupported feature inherited another grant");
    }

    private static void Cleanup()
    {
        object state = Construct("CosmeticEffectState", Enum("LocalizedEffectKind", 1), null);
        ZNetView.m_forceDisableInit = true;
        Invoke("CosmeticEffectListCreateCleanup", state);
        Check(!ZNetView.m_forceDisableInit, "Committed local effect did not restore ZNetView init");
        state = Construct("CosmeticEffectState", Enum("LocalizedEffectKind", 0), null);
        ZNetView.m_forceDisableInit = true;
        Invoke("CosmeticEffectListCreateCleanup", state);
        Check(ZNetView.m_forceDisableInit, "Unowned nested context was reset");
        ZNetView.m_forceDisableInit = false;
        Invoke("ClearCosmeticFeatureState");
        Check(InteropFixes.CosmeticNetworkCoordinator.Reported == 0, "Scene cleanup did not revoke local capabilities");
    }

    private static void CompiledBoundaries(string assemblyPath)
    {
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        TypeDefinition type = assembly.MainModule.Types.Single(t => t.Name == "MagicPluginCosmeticZdoPrevention");
        bool Calls(string method, string target) => type.Methods.Single(m => m.Name == method).Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == target);
        Check(Calls("TryCaptureCosmeticEvent", "CanOptimizeFeature"), "Event emission lost its agreement gate");
        Check(!Calls("ReceiveCosmeticEvent", "CanOptimize") && !Calls("ReceiveCosmeticEvent", "CanOptimizeFeature"), "Committed receiver depends on current agreement");
        Check(!Calls("BroadcastCosmeticEvent", "CanOptimize") && !Calls("BroadcastCosmeticEvent", "CanOptimizeFeature"), "Committed notification depends on current agreement");
        Check(!Calls("CosmeticNetworkObjectCreatePostfix", "CanOptimizeFeature"), "Existing carrier replay depends on current agreement");
        Check(!type.Methods.Any(m => m.Name == "ProjectileHitRpcPrefix" || m.Name == "CosmeticProjectileHitPostfix"), "Native hit RPC still interpreted as an optimization decision");
        Check(!type.Methods.Any(m => m.Name == "CosmeticIceblockAwakePostfix" || m.Name == "CosmeticZdoDestroyedPrefix"), "Creation/death marker still authorizes Arctic replay");
        Check(type.Methods.SelectMany(m => m.HasBody ? m.Body.Instructions : Enumerable.Empty<Instruction>()).Any(i =>
            i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "BlastedSwimmingTarredBugFix_MagicCosmeticCarrier")), "Existing marker key changed");
        Check(AccessTools.DeclaredMethod(typeof(EffectList), "Create")!.GetParameters()[0].Name == "basePos" &&
              AccessTools.DeclaredMethod(typeof(EffectList), "Create")!.GetParameters()[1].Name == "baseRot", "Effect position/rotation patch arguments do not match original metadata");
    }
}
