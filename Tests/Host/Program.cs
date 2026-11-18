using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;

internal static class Program
{
    private static int _checks;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int Main(string[] args)
    {
        string game = args.Length > 0 ? args[0] :
            @"C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed";
        string core = @"C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\core";
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string root in new[] { game, core })
                if (File.Exists(Path.Combine(root, name))) return Assembly.LoadFrom(Path.Combine(root, name));
            return null;
        };
        try
        {
            string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            Assembly mod = Assembly.LoadFrom(Path.Combine(project, "bin/Debug/netstandard2.1/InteropFixes.dll"));
            Type plugin = mod.GetType("InteropFixes.InteropFixesPlugin", true)!;
            Type isolation = mod.GetType("InteropFixes.ModuleIsolation", true)!;
            Assembly bep = Assembly.LoadFrom(Path.Combine(core, "BepInEx.dll"));
            string sandbox = Path.Combine(Path.GetTempPath(), "interop-host-" + Guid.NewGuid().ToString("N"));
            bep.GetType("BepInEx.Paths", true)!.GetMethod("SetExecutablePath", Static)!.Invoke(null,
                new object[] { Path.Combine(sandbox, "valheim.exe"), Path.Combine(sandbox, "BepInEx"), core, new[] { game, core } });
            object logger = Activator.CreateInstance(bep.GetType("BepInEx.Logging.ManualLogSource", true)!, "InteropTests")!;
            plugin.GetProperty("ModLogger", Static)!.SetValue(null, logger);

            string[] names = mod.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
            foreach (string optional in new[] { "VNEI", "PathOfValheiman", "EpicLoot", "AzuCraftyBoxes", "MagicPlugin", "ServerSync" })
                Check(!names.Contains(optional), "no mandatory reference: " + optional);
            Type[] all = mod.GetTypes();
            Check(all.Any(t => t.Name == "VneiItemLookupModule"), "VNEI module resolves without VNEI");
            Check(all.Any(t => t.Name == "PathOfValheimanLookupModule"), "PoV lookup module resolves without PoV/VNEI");
            Check(all.Any(t => t.Name == "EpicLootCraftyBoxesModule"), "bridge resolves without EpicLoot/ACB");
            Check(all.All(t => t.Name != "CharacterLifecycleCompatibility" && t.Name != "CreatureRemovalTracker"), "generic Character module removed");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => new[] { "VNEI", "PathOfValheiman", "EpicLoot", "AzuCraftyBoxes", "MagicPlugin" }.Contains(a.GetName().Name)), "optional DLLs never loaded");

            var dependencies = plugin.GetCustomAttributesData().Where(a => a.AttributeType.Name == "BepInDependency").ToArray();
            Check(dependencies.Length == 8, "eight optional dependency ordering declarations");
            Check(dependencies.Any(a => Equals(a.ConstructorArguments[0].Value, "treextr.pathofvalheiman")), "PoV has an optional load-order declaration");
            foreach (var dependency in dependencies)
                Check(dependency.ConstructorArguments.Count == 2 && Convert.ToInt32(dependency.ConstructorArguments[1].Value) == 2,
                    "soft dependency: " + dependency.ConstructorArguments[0].Value);

            MethodInfo legacy = isolation.GetMethod("IsLegacyPatch", Static)!;
            bool Legacy(string owner, string? patch, string? standalone, bool magic) =>
                (bool)legacy.Invoke(null, new object?[] { owner, patch, standalone, magic })!;
            const string blasted = "sighsorry.blastedswimmingtarredbugfix";
            const string vnei = "sighsorry.VneiItemLookupFix";
            Check(Legacy(blasted + ".MagicPlugin", null, null, true), "old dedicated Magic owner detected");
            Check(Legacy(blasted, "BlastedSwimmingTarredBugFix.MagicPluginCompatibility", null, true), "older shared owner detected by specific patch type");
            Check(!Legacy(blasted + ".RuntimeOptimizations", "BlastedSwimmingTarredBugFix.RuntimeOptimizations", null, true), "current Blasted runtime does not block Magic");
            Check(!Legacy(blasted + ".MagicPlugin", null, vnei, false), "Magic legacy does not block VNEI");
            Check(Legacy(vnei, null, vnei, false), "standalone exact owner detected");
            Check(Legacy(vnei + ".Lookup", null, vnei, false), "standalone subowner detected");
            Check(!Legacy(vnei + "Other", null, vnei, false), "unrelated similar prefix allowed");

            MethodInfo gate = isolation.GetMethod("CanInstall", Static)!;
            Check(!(bool)gate.Invoke(null, new object?[] { "absent", new[] { "tests.missing.target" }, null, false })!, "absent target leaves host available");
            Check((bool)gate.Invoke(null, new object?[] { "empty control", Array.Empty<string>(), null, false })!, "no legacy hooks allows independent module");
            var registry = (System.Collections.IDictionary)bep.GetType("BepInEx.Bootstrap.Chainloader", true)!
                .GetProperty("PluginInfos", Static)!.GetValue(null);
            object legacyInfo = Activator.CreateInstance(bep.GetType("BepInEx.PluginInfo", true)!, true)!;
            registry.Add(vnei, legacyInfo);
            try
            {
                Check(!(bool)gate.Invoke(null, new object?[] { "legacy VNEI", Array.Empty<string>(), vnei, false })!,
                    "registered standalone refuses duplicate even before it installs hooks");
                Check((bool)gate.Invoke(null, new object?[] { "bridge", Array.Empty<string>(), "sighsorry.EpicLootCraftyBoxesBridge", false })!,
                    "standalone VNEI does not block bridge");
            }
            finally { registry.Remove(vnei); }

            // Real Harmony ownership and rollback checks on managed-only methods.
            var first = new Harmony("InteropTests.First");
            var second = new Harmony("InteropTests.Second");
            var old = new Harmony(blasted + ".MagicPlugin");
            MethodInfo probe = typeof(Program).GetMethod(nameof(Probe), Static)!;
            try
            {
                first.Patch(probe, postfix: new HarmonyMethod(typeof(Program), nameof(AddOne)));
                second.Patch(probe, postfix: new HarmonyMethod(typeof(Program), nameof(AddTwo)));
                Check(Probe() == 10, "two independent modules active");
                MethodInfo remove = isolation.GetMethod("TryUnpatch", Static)!;
                Check((bool)remove.Invoke(null, new object[] { first, "first" })!, "first module teardown verified");
                Check(Probe() == 9, "second module preserved");
                Check((bool)remove.Invoke(null, new object[] { first, "first again" })!, "teardown idempotent");
                old.Patch(probe, postfix: new HarmonyMethod(typeof(Program), nameof(AddOne)));
                Check(!(bool)gate.Invoke(null, new object?[] { "Magic", Array.Empty<string>(), null, true })!, "actual old Magic patch refuses new Magic");
                Check((bool)gate.Invoke(null, new object?[] { "VNEI", Array.Empty<string>(), vnei, false })!, "actual old Magic patch allows VNEI");
                int rolledBack = 0;
                plugin.GetMethod("TryInitialize", Static)!.Invoke(null, new object[] {
                    "failing module", (Action)(() => throw new InvalidOperationException("expected fixture failure")),
                    (Action)(() => rolledBack++)
                });
                Check(rolledBack == 1, "failed installation invokes module rollback once");
                Check(Harmony.GetPatchInfo(probe)!.Owners.Contains(second.Id), "failure does not unpatch sibling");
            }
            finally { first.UnpatchSelf(); second.UnpatchSelf(); old.UnpatchSelf(); }
            Check(Probe() == 7, "all fixture patches restored");

            // No Unity constructors/native methods: absent modules can be shut down safely.
            object unstarted = FormatterServices.GetUninitializedObject(plugin);
            plugin.GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(unstarted, null);
            plugin.GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(unstarted, null);
            Check(true, "absent-module shutdown repeatable");
            Console.WriteLine($"PASS: {_checks} final-DLL isolation/metadata/Harmony assertions. Unity plugin startup/gameplay not run.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static int Probe() => 7;
    private static void AddOne(ref int __result) => __result++;
    private static void AddTwo(ref int __result) => __result += 2;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        _checks++;
    }
}
