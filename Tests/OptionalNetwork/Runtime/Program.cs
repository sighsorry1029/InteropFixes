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
        string sandbox = Path.Combine(Path.GetTempPath(), "InteropOptionalNetwork", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        typeof(Paths).GetMethod("SetExecutablePath", F)!.Invoke(null,
            new object[] { Path.Combine(sandbox, "valheim.exe"), Path.Combine(sandbox, "BepInEx"), args[1], new[] { args[1], args[2] } });
        // Deliberately no ServerSyncHost/BepInEx threading initializer: Awake must not depend on it.
        mono_add_internal_call("UnityEngine.Object::GetOffsetOfInstanceIDInCPlusPlusObject", Marshal.GetFunctionPointerForDelegate(Offset));
        try { return (int)typeof(Program).Assembly.GetType("Checks", true)!.GetMethod("Run", F)!.Invoke(null, new object[] { args[0], sandbox })!; }
        catch (Exception e) { System.Console.Error.WriteLine(e); return 1; }
    }
}

internal static class Checks
{
    private const BindingFlags F = Program.F;
    private const string Owner = "sighsorry.InteropFixes.CosmeticNetwork";
    private const string ForeignOwner = "InteropTests.OptionalNetwork.Foreign";
    private static int checks;
    private static readonly List<IntPtr> Tokens = new();
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    private static object? Field(Type type, string name, object? owner = null) => AccessTools.Field(type, name).GetValue(owner);
    private static void Set(Type type, string name, object? value, object? owner = null) => AccessTools.Field(type, name).SetValue(owner, value);
    private static bool Property(Type type, string name) => (bool)type.GetProperty(name, F)!.GetValue(null)!;
    private static object Raw(Type type) => FormatterServices.GetUninitializedObject(type);
    private static object Alive(Type type)
    {
        object value = Raw(type);
        IntPtr token = Marshal.AllocHGlobal(4); Marshal.WriteInt32(token, Tokens.Count + 1); Tokens.Add(token);
        Set(typeof(UnityEngine.Object), "m_CachedPtr", token, value);
        return value;
    }
    private static bool Log() => false;
    private static void ForeignPrefix() { }
    private static bool HasOwner(MethodBase method, string owner) => Harmony.GetPatchInfo(method)?.Owners.Contains(owner) == true;

    internal static int Run(string dll, string sandbox)
    {
        Assembly mod = Assembly.LoadFrom(dll);
        Type pluginType = mod.GetType("InteropFixes.InteropFixesPlugin", true)!;
        Type coordinator = mod.GetType("InteropFixes.CosmeticNetworkCoordinator", true)!;
        Check(!mod.GetReferencedAssemblies().Any(a => a.Name == "ServerSync") && !mod.GetTypes().Any(t => t.FullName!.StartsWith("ServerSync.")), "Loaded artifact has no external or merged ServerSync dependency.");
        var substitutes = new Harmony("InteropTests.OptionalNetwork.Substitutes");
        var foreign = new Harmony(ForeignOwner);
        object plugin = Alive(pluginType);
        MethodInfo awake = pluginType.GetMethod("Awake", F)!, destroy = pluginType.GetMethod("OnDestroy", F)!;
        MethodInfo[] expected = {
            AccessTools.DeclaredMethod(typeof(ZNet), "Awake", Type.EmptyTypes),
            AccessTools.DeclaredMethod(typeof(ZNet), "OnNewConnection", new[] { typeof(ZNetPeer) }),
            AccessTools.DeclaredMethod(typeof(ZNet), "RPC_PeerInfo", new[] { typeof(ZRpc), typeof(ZPackage) }),
            AccessTools.DeclaredMethod(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) }),
            AccessTools.DeclaredMethod(typeof(ZNet), "StopAll", new[] { typeof(bool) }),
            AccessTools.DeclaredMethod(typeof(ZNetScene), "Awake", Type.EmptyTypes),
            AccessTools.DeclaredMethod(typeof(ZNetScene), "OnDestroy", Type.EmptyTypes)
        };
        try
        {
            foreach (string name in new[] { "Log", "LogWarning", "LogError" })
                substitutes.Patch(AccessTools.DeclaredMethod(typeof(Debug), name, new[] { typeof(object) }), prefix: new HarmonyMethod(typeof(Checks), nameof(Log)));
            foreign.Patch(expected[3], prefix: new HarmonyMethod(typeof(Checks), nameof(ForeignPrefix)));
            Set(typeof(BaseUnityPlugin), "<Logger>k__BackingField", BepInEx.Logging.Logger.CreateLogSource("InteropOptionalNetwork"), plugin);
            string cfgPath = Path.Combine(sandbox, "sighsorry.InteropFixes.cfg");
            File.WriteAllText(cfgPath, "[MagicPlugin]\nEnableNetworkCosmeticOptimization = false\n");
            var config = new ConfigFile(cfgPath, false);
            Set(typeof(BaseUnityPlugin), "<Config>k__BackingField", config, plugin);
            awake.Invoke(plugin, null);
            Check(config.Count == 0, "Actual Awake must leave configuration empty.");
            Check(!config.ContainsKey(new ConfigDefinition("MagicPlugin", "EnableNetworkCosmeticOptimization")), "Legacy false setting must remain unbound.");
            Check(Property(pluginType, "CosmeticRuntimeActive"), "Automatic eligibility must activate independently of legacy false cfg.");
            Check((bool)Field(coordinator, "_installed")!, "Actual coordinator install must succeed without a ServerSync initializer.");
            MethodBase[] owned = Harmony.GetAllPatchedMethods().Where(m => HasOwner(m, Owner)).ToArray();
            Check(owned.Length == 7 && expected.All(owned.Contains), "Awake must install exactly the seven game lifecycle targets.");
            foreach (MethodInfo target in expected)
            {
                Patches patches = Harmony.GetPatchInfo(target);
                Check(patches.Prefixes.Where(p => p.owner == Owner).All(p => p.priority == Priority.First && p.PatchMethod.ReturnType == typeof(void)) &&
                    patches.Postfixes.Where(p => p.owner == Owner).All(p => p.priority == Priority.Last), "Lifecycle observation must preserve original execution and reviewed ordering: " + target.DeclaringType!.Name + "." + target.Name);
            }
            Check(!Harmony.GetPatchInfo(expected[2]).Prefixes.Any(p => p.owner == Owner), "PeerInfo must not receive a mod-version admission veto prefix.");
            Check(!Harmony.GetAllPatchedMethods().SelectMany(m => Harmony.GetPatchInfo(m).Owners).Any(o => o.StartsWith("ServerSync", StringComparison.OrdinalIgnoreCase)), "Awake must not install ServerSync version/disconnect hooks.");
            Check(!(bool)coordinator.GetMethod("CanOptimize", F)!.Invoke(null, new object[] { 1 })!, "No network/scene capability agreement must preserve native FX creation.");

            // Original ZRpc.Register/Unregister execute against its original private dictionary.
            ZNet net = (ZNet)Alive(typeof(ZNet));
            Set(typeof(ZNet), "m_instance", net); Set(typeof(ZNet), "m_isServer", false); Set(coordinator, "_net", net);
            ZRpc rpc = (ZRpc)Raw(typeof(ZRpc));
            FieldInfo functionsField = AccessTools.Field(typeof(ZRpc), "m_functions");
            IDictionary functions = (IDictionary)Activator.CreateInstance(functionsField.FieldType)!;
            functionsField.SetValue(rpc, functions);
            ZNetPeer peer = (ZNetPeer)Raw(typeof(ZNetPeer)); peer.m_rpc = rpc; peer.m_server = true;
            rpc.Register<ZPackage>("test.unrelated", (_, __) => { });
            coordinator.GetMethod("BeforeNewConnection", F)!.Invoke(null, new object[] { net, peer });
            Check(functions.Count == 4 && ((IDictionary)Field(coordinator, "Connections")!).Count == 1, "Actual private-field access and original RPC registration must add only the three capability receivers.");
            Check(!(bool)coordinator.GetMethod("CanOptimize", F)!.Invoke(null, new object[] { 1 })!, "Registering receivers alone must not grant optimization.");
            string hello = "sighsorry.InteropFixes.Cosmetics.Hello.v2";
            rpc.Register<ZPackage>(hello, (_, __) => { });
            object replacement = functions[Hash(coordinator, hello)]!;
            destroy.Invoke(plugin, null);
            Check(functions.Count == 2 && ReferenceEquals(functions[Hash(coordinator, hello)], replacement), "Teardown must preserve unrelated and externally replaced handlers while removing its own Report/Grant receivers.");
            Check(((IDictionary)Field(coordinator, "Connections")!).Count == 0 && !(bool)Field(coordinator, "_installed")!, "Teardown must clear connection membership and installation state.");
            Check(!Property(pluginType, "CosmeticRuntimeActive"), "Teardown must end automatic eligibility.");
            Check(!Harmony.GetAllPatchedMethods().Any(m => HasOwner(m, Owner)), "Teardown must remove every owned lifecycle patch.");
            Check(HasOwner(expected[3], ForeignOwner), "Teardown must preserve another mod's disconnect hook.");
            destroy.Invoke(plugin, null);
            Check(functions.Count == 2 && !Property(pluginType, "CosmeticRuntimeActive"), "Repeated teardown must remain harmless.");
            System.Console.WriteLine($"PASS: {checks} optional-install final DLL runtime checks. Actual Awake, Harmony lifecycle install/remove, original RPC registration and ownership cleanup. No live transport or Unity scene execution.");
            return 0;
        }
        finally
        {
            destroy.Invoke(plugin, null);
            foreign.UnpatchSelf(); substitutes.UnpatchSelf();
            Set(typeof(ZNet), "m_instance", null); Set(typeof(ZNet), "m_isServer", true);
            foreach (IntPtr token in Tokens) Marshal.FreeHGlobal(token);
        }
    }

    private static int Hash(Type coordinator, string name) => (int)coordinator.GetMethod("StableHash", F)!.Invoke(null, new object[] { name })!;
}
