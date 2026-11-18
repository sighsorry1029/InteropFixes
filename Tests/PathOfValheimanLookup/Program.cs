using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using HarmonyLib;
using InteropFixes.Modules.PathOfValheimanLookup;
using Mono.Cecil;

internal static class Program
{
    private static int checks;
    private static readonly string Sandbox = Path.Combine(Path.GetTempPath(), "InteropLookupTests", Guid.NewGuid().ToString("N"));
    private static readonly MethodInfo TypeByName = typeof(AccessTools).GetMethod("TypeByName", new[] { typeof(string) })!;
    private static readonly MethodInfo Field = typeof(AccessTools).GetMethod("Field", new[] { typeof(Type), typeof(string) })!;
    private static readonly MethodInfo Resolve = typeof(OptionalTypeLookup).GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
    private static readonly MethodInfo SafeField = typeof(OptionalTypeLookup).GetMethod("FieldIfPresent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
    private static string[] SearchDirectories = Array.Empty<string>();
    private const string Owner = "InteropTests.PovLookup";
    [DllImport("mono-2.0-sgen.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_add_internal_call(string name, IntPtr callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OffsetCall();
    private static readonly OffsetCall Offset = () => 0;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message + "\n" + string.Join("\n", InteropFixes.InteropFixesPlugin.ModLogger.Messages));
        checks++;
    }

    private static int Main(string[] args)
    {
        if (args.Length < 4 || args.Length > 5)
            throw new ArgumentException("Usage: <original PoV.dll> <original VNEI.dll> <original Managed> <BepInEx core> [final InteropFixes.dll]");
        Directory.CreateDirectory(Sandbox);
        string[] pluginDlls = Directory.GetFiles(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(args[0])))!, "*.dll", SearchOption.AllDirectories);
        SearchDirectories = new[] { args[2], args[3], AppDomain.CurrentDomain.BaseDirectory }.Concat(pluginDlls.Select(Path.GetDirectoryName)!).Distinct().ToArray()!;
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string root in new[] { args[2], args[3], AppDomain.CurrentDomain.BaseDirectory })
            {
                string candidate = Path.Combine(root, name);
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            string? plugin = pluginDlls.FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
            return plugin == null ? null : Assembly.LoadFrom(plugin);
        };
        try
        {
            LookupChecks();
            OriginalIlChecks(args[0]);
            LifecycleChecks(args[0], args[2], args[3]);
            ArtifactChecks(args[0], args[1], args.Length == 5 ? args[4] : null);
            System.Console.WriteLine($"PASS: {checks} PoV/VNEI lookup checks. Original metadata/IL and isolated managed reflection; no Unity scene or live game session.");
            return 0;
        }
        catch (Exception error)
        {
            System.Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void LookupChecks()
    {
        string name = "InteropLookupPoison_" + Guid.NewGuid().ToString("N");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new System.Version(1, 0, 0, 0)), name, ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var missing = new AssemblyNameReference("InteropDefinitelyAbsentDependency_" + Guid.NewGuid().ToString("N"), new System.Version(1, 0));
            module.AssemblyReferences.Add(missing);
            module.Types.Add(new TypeDefinition("InteropLookupFixture", "Healthy", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object));
            module.Types.Add(new TypeDefinition("InteropLookupFixture", "Broken", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                new TypeReference("MissingDependency", "MissingBase", module, missing)));
            assembly.Write(Path.Combine(Sandbox, name + ".dll"));
        }
        Assembly loaded = Assembly.LoadFrom(Path.Combine(Sandbox, name + ".dll"));
        bool failed = false;
        try { loaded.GetTypes(); }
        catch (ReflectionTypeLoadException) { failed = true; }
        Check(failed, "The poisoned assembly really fails whole-assembly type enumeration.");
        Type healthy = loaded.GetType("InteropLookupFixture.Healthy", true)!;
        Check(OptionalTypeLookup.Resolve("InteropLookupFixture.Healthy") == healthy, "Exact lookup resolves a healthy type even when another type has a missing dependency.");
        int before = InteropFixes.InteropFixesPlugin.ModLogger.Messages.Count;
        Check(OptionalTypeLookup.Resolve("InteropLookupFixture.Absent") == null, "Absent exact type returns null despite unrelated broken types.");
        Check(OptionalTypeLookup.Resolve("Healthy") == null, "A simple-name fallback cannot accidentally choose an unrelated type.");
        Check(InteropFixes.InteropFixesPlugin.ModLogger.Messages.Count == before, "Ordinary optional absence is silent.");
        Type? broken = OptionalTypeLookup.Resolve("InteropLookupFixture.Broken");
        if (broken == null)
        {
            int after = InteropFixes.InteropFixesPlugin.ModLogger.Messages.Count;
            Check(after > before, "A requested-type lookup failure is diagnosed separately from optional absence.");
            Check(OptionalTypeLookup.Resolve("InteropLookupFixture.Broken") == null && InteropFixes.InteropFixesPlugin.ModLogger.Messages.Count == after,
                "Repeated requested-type lookup failures do not repeat diagnostics.");
        }
        else
        {
            // Unity Mono can return a RuntimeType before resolving its missing
            // base class. The helper promises exact lookup, not eager validation
            // of every member/dependency of a successfully returned type.
            Check(broken.FullName == "InteropLookupFixture.Broken", "Lazy Mono type resolution still returns only the exact requested identity.");
            Check(InteropFixes.InteropFixesPlugin.ModLogger.Messages.Count == before, "A successful lazy GetType result is not mislabeled as a lookup failure.");
        }
        Check(OptionalTypeLookup.FieldIfPresent(null, "Entries") == null, "Missing SeneaL type does not call AccessTools.Field(null).");
        Check(OptionalTypeLookup.FieldIfPresent(typeof(FieldOwner), "Entries") == typeof(FieldOwner).GetField("Entries", BindingFlags.NonPublic | BindingFlags.Static),
            "Present private field uses the original Harmony field contract.");
        Check(OptionalTypeLookup.FieldIfPresent(typeof(FieldDerived), "Entries") == typeof(FieldOwner).GetField("Entries", BindingFlags.NonPublic | BindingFlags.Static),
            "Inherited private field lookup retains Harmony behavior.");

        // Missing results must not be cached by InteropFixes: PoV owns its lookup
        // boundary, while a module registered later can still be found there.
        const string lateType = "InteropLookupFixture.LateRegistered";
        Check(OptionalTypeLookup.Resolve(lateType) == null, "A not-yet-registered type starts absent.");
        var emitted = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("InteropLateTypes_" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        Type late = emitted.DefineDynamicModule("Types").DefineType(lateType, System.Reflection.TypeAttributes.Public).CreateType()!;
        Check(OptionalTypeLookup.Resolve(lateType) == late, "Exact lookup sees newly loaded assemblies without a stale negative cache.");
    }

    private class FieldOwner { private static readonly object Entries = new(); }
    private sealed class FieldDerived : FieldOwner { }

    private static void OriginalIlChecks(string path)
    {
        Assembly assembly = Assembly.LoadFrom(path);
        MethodInfo[] targets = OptionalLookupPatch.GetTargets(assembly);
        Check(targets.Length == 4, "Original PoV supplies exactly the four reviewed targets.");
        foreach (MethodInfo target in targets)
        {
            List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(target).ToList();
            Check(OptionalLookupPatch.TryRewrite(target, code, out List<CodeInstruction> rewritten, out string reason), "Original PoV lookup flow accepted: " + target + " / " + reason);
            Check(rewritten.Count == code.Count, "Lookup rewrite never removes gameplay instructions: " + target);
            int expectedTypes = target.Name == "get_ExtraSlots" ? 2 : 1;
            Check(code.Count(i => i.Calls(TypeByName)) == expectedTypes && rewritten.Count(i => i.Calls(Resolve)) == expectedTypes,
                "All reviewed type lookups are replaced together: " + target);
            int expectedFields = target.Name == "Postfix" ? 1 : 0;
            Check(rewritten.Count(i => i.Calls(SafeField)) == expectedFields, "Only the SeneaL null-sensitive field call is guarded: " + target);
            Check(code.Zip(rewritten, (before, after) => before.opcode == after.opcode &&
                before.labels.SequenceEqual(after.labels) && before.blocks.SequenceEqual(after.blocks) &&
                (Equals(before.operand, after.operand) || before.Calls(TypeByName) && after.Calls(Resolve) || before.Calls(Field) && after.Calls(SafeField))).All(x => x),
                "Every gameplay operand, branch and exception boundary survives the lookup rewrite: " + target);
            Check(code.All(i => !i.Calls(Resolve) && !i.Calls(SafeField)), "Source instruction list remains unchanged: " + target);

            List<CodeInstruction> unrelated = Copy(code);
            unrelated.Insert(0, new CodeInstruction(System.Reflection.Emit.OpCodes.Nop));
            Check(OptionalLookupPatch.TryRewrite(target, unrelated, out _, out _), "Unrelated instructions do not disable the integration: " + target);

            var labeledNop = Copy(code);
            int lookupForLabel = code.FindIndex(i => i.Calls(TypeByName));
            Label cacheDestination = (Label)code.Take(lookupForLabel).Last(i => i.opcode == System.Reflection.Emit.OpCodes.Brtrue || i.opcode == System.Reflection.Emit.OpCodes.Brtrue_S).operand;
            int labeled = labeledNop.FindIndex(i => i.labels.Contains(cacheDestination));
            Check(labeled >= 0, "Original method has a branch destination for no-op normalization: " + target);
            var destination = new CodeInstruction(System.Reflection.Emit.OpCodes.Nop);
            destination.labels.AddRange(labeledNop[labeled].labels);
            labeledNop[labeled].labels.Clear();
            labeledNop.Insert(labeled, destination);
            Check(OptionalLookupPatch.TryRewrite(target, labeledNop, out var labeledResult, out _) && labeledResult[labeled].labels.SequenceEqual(destination.labels),
                "Labels attached to an unrelated no-op remain valid and are preserved: " + target);

            int firstLookup = code.FindIndex(i => i.Calls(TypeByName));
            int nameIndex = firstLookup - 1;
            while (nameIndex >= 0 && code[nameIndex].opcode == System.Reflection.Emit.OpCodes.Nop) nameIndex--;
            Check(nameIndex >= 0 && code[nameIndex].opcode == System.Reflection.Emit.OpCodes.Ldstr, "Lookup is fed by its reviewed exact name: " + target);
            var wrongName = Copy(code);
            wrongName[nameIndex].operand = "DifferentMod.DifferentResponsibility";
            Check(!OptionalLookupPatch.TryRewrite(target, wrongName, out var rejected, out _) && Same(wrongName, rejected), "Changed lookup responsibility rejects atomically: " + target);
            var removedLookup = Copy(code);
            removedLookup[firstLookup].operand = typeof(Program).GetMethod(nameof(OtherLookup), BindingFlags.NonPublic | BindingFlags.Static);
            Check(!OptionalLookupPatch.TryRewrite(target, removedLookup, out rejected, out _) && Same(removedLookup, rejected), "A foreign lookup replacement rejects atomically: " + target);
            var removedCache = Copy(code);
            int cached = removedCache.FindLastIndex(firstLookup, i => i.opcode == System.Reflection.Emit.OpCodes.Stsfld && i.operand is FieldInfo f && f.FieldType == typeof(bool));
            removedCache[cached].opcode = System.Reflection.Emit.OpCodes.Pop;
            removedCache[cached].operand = null;
            Check(!OptionalLookupPatch.TryRewrite(target, removedCache, out rejected, out _) && Same(removedCache, rejected), "Changed one-time cache responsibility rejects atomically: " + target);
            if (target.Name == "Postfix")
            {
                var changedCleanup = Copy(code);
                CodeInstruction pairs = changedCleanup.First(i => i.operand is MethodInfo m && m.Name == "Pairs");
                pairs.operand = typeof(Program).GetMethod(nameof(OtherPairs), BindingFlags.NonPublic | BindingFlags.Static);
                Check(!OptionalLookupPatch.TryRewrite(target, changedCleanup, out rejected, out _) && Same(changedCleanup, rejected), "Changed native recipe cleanup responsibility rejects without dropping the callback body.");
            }
        }

        // A bytewise different rebuild with unrelated identity changes must be
        // admitted by its actual contract, not by a DLL hash/MVID/version gate.
        using var resolver = new DefaultAssemblyResolver();
        foreach (string directory in SearchDirectories) resolver.AddSearchDirectory(directory);
        using (var rebuilt = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver }))
        {
            rebuilt.Name.Name += "_UnrelatedRebuild";
            rebuilt.Name.Version = new System.Version(91, 17, 4, 2);
            rebuilt.MainModule.Mvid = Guid.NewGuid();
            using var bytes = new MemoryStream();
            rebuilt.Write(bytes);
            Assembly changedIdentity = Assembly.Load(bytes.ToArray());
            foreach (MethodInfo target in OptionalLookupPatch.GetTargets(changedIdentity))
                Check(OptionalLookupPatch.TryRewrite(target, PatchProcessor.GetOriginalInstructions(target), out _, out _), "Unrelated assembly version/MVID/name rebuild is accepted: " + target);
        }
    }

    private static void LifecycleChecks(string povPath, string managed, string core)
    {
        var registry = BepInEx.Bootstrap.Chainloader.PluginInfos;
        var own = new Harmony(Owner);
        var foreign = new Harmony(Owner + ".Foreign");
        Check(!PathOfValheimanLookupModule.TryInstall(own), "Missing optional mods install no hooks.");
        registry[PathOfValheimanLookupModule.VneiGuid] = new BepInEx.Bootstrap.PluginInfo();
        Check(!PathOfValheimanLookupModule.TryInstall(own), "VNEI alone installs no PoV lookup hooks.");
        registry[PathOfValheimanLookupModule.PovGuid] = new BepInEx.Bootstrap.PluginInfo { Instance = new object() };
        Check(!PathOfValheimanLookupModule.TryInstall(own), "An unexpected PoV entry type is rejected without affecting other integrations.");

        // This is only an uninitialized identity object. PoV Awake/Start and all
        // game callbacks remain unexecuted. Its static path initialization sees
        // an empty sandbox rather than the user's BepInEx config directory.
        Assembly bep = Assembly.LoadFrom(Path.Combine(core, "BepInEx.dll"));
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        bep.GetType("BepInEx.Paths", true)!.GetMethod("SetExecutablePath", all)!.Invoke(null,
            new object[] { Path.Combine(Sandbox, "valheim.exe"), Path.Combine(Sandbox, "BepInEx"), managed, new[] { managed, core } });
        if (Type.GetType("Mono.Runtime") != null)
            mono_add_internal_call("UnityEngine.Object::GetOffsetOfInstanceIDInCPlusPlusObject", Marshal.GetFunctionPointerForDelegate(Offset));
        Assembly pov = Assembly.LoadFrom(povPath);
        object identity = FormatterServices.GetUninitializedObject(pov.GetType("PassiveTree.Plugin", true)!);
        registry[PathOfValheimanLookupModule.PovGuid] = new BepInEx.Bootstrap.PluginInfo { Instance = identity };
        MethodInfo[] targets = OptionalLookupPatch.GetTargets(pov);
        registry.Remove(PathOfValheimanLookupModule.VneiGuid);
        Check(!PathOfValheimanLookupModule.TryInstall(own), "PoV alone installs no joint lookup integration.");
        registry[PathOfValheimanLookupModule.VneiGuid] = new BepInEx.Bootstrap.PluginInfo();
        registry["sighsorry.VneiItemLookupFix"] = new BepInEx.Bootstrap.PluginInfo();

        try
        {
            // Break only the final target so the first three successful installs
            // must be rolled back while this foreign transform survives.
            foreign.Patch(targets[3], transpiler: new HarmonyMethod(typeof(Program), nameof(ForeignLookupChange)));
            Check(!PathOfValheimanLookupModule.TryInstall(own), "A changed active final target rejects partial module installation.");
            Check(!HasOwner(Owner), "Failed installation rolls back all previously installed lookup hooks.");
            Check(Harmony.GetPatchInfo(targets[3]).Owners.Contains(foreign.Id), "Rollback preserves the foreign target transform.");
            foreign.UnpatchSelf();
            foreign.Patch(targets[0], prefix: new HarmonyMethod(typeof(Program), nameof(ForeignPrefix)));
            Check(PathOfValheimanLookupModule.TryInstall(own), "Original PoV installs even with the standalone VNEI item fix present.");
            Check(PathOfValheimanLookupModule.TryInstall(own), "Repeated installation is idempotent.");
            Check(Harmony.GetAllPatchedMethods().Count(m => Harmony.GetPatchInfo(m).Owners.Contains(Owner)) == 4,
                "The module owns exactly its four PoV target methods.");
            foreach (MethodInfo target in targets)
            {
                Patches patches = Harmony.GetPatchInfo(target);
                Check(patches.Transpilers.Count(p => p.owner == Owner) == 1 &&
                    patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Finalizers).All(p => p.owner != Owner),
                    "The module installs one narrow transpiler and no gameplay callback replacement: " + target);
                Check(PatchProcessor.GetCurrentInstructions(target).Any(i => i.Calls(Resolve)), "The actual Harmony method body contains the production resolver: " + target);
            }
            Check(Harmony.GetPatchInfo(targets[0]).Owners.Contains(foreign.Id), "Successful installation preserves other owners.");
            foreign.Patch(targets[3], transpiler: new HarmonyMethod(typeof(Program), nameof(ForeignLookupChange)));
            Check(!OptionalLookupPatch.WasAccepted(targets[3]) && !PatchProcessor.GetCurrentInstructions(targets[3]).Any(i => i.Calls(Resolve)),
                "A later foreign transformation disables only the changed target's lookup replacement.");
            Check(targets.Take(3).All(t => PatchProcessor.GetCurrentInstructions(t).Any(i => i.Calls(Resolve))), "The other independent lookup paths remain active after one target changes.");
            PathOfValheimanLookupModule.Stop();
            Check(!HasOwner(Owner), "Stop removes all four owned transpilers.");
            Check(Harmony.GetPatchInfo(targets[0]).Owners.Contains(foreign.Id), "Stop preserves the foreign prefix.");
            PathOfValheimanLookupModule.Stop();
            Check(!HasOwner(Owner), "Repeated Stop is harmless.");
        }
        finally
        {
            PathOfValheimanLookupModule.Stop();
            foreign.UnpatchSelf();
            registry.Clear();
        }
    }

    private static bool HasOwner(string owner) => Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m).Owners.Contains(owner));
    private static void ForeignPrefix() { }
    private static IEnumerable<CodeInstruction> ForeignLookupChange(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            var copy = new CodeInstruction(instruction);
            if (copy.opcode == System.Reflection.Emit.OpCodes.Ldstr && Equals(copy.operand, "SeneaLUI.Inv.CraftState"))
                copy.operand = "DifferentMod.DifferentResponsibility";
            yield return copy;
        }
    }

    private static Type? OtherLookup(string name) => null;
    private static bool OtherPairs(Recipe recipe, ItemDrop.ItemData item) => false;
    private static List<CodeInstruction> Copy(IEnumerable<CodeInstruction> code) => code.Select(i => new CodeInstruction(i)).ToList();
    private static bool Same(IReadOnlyList<CodeInstruction> a, IReadOnlyList<CodeInstruction> b) => a.Count == b.Count &&
        a.Zip(b, (x, y) => x.opcode == y.opcode && Equals(x.operand, y.operand) && x.labels.SequenceEqual(y.labels) && x.blocks.SequenceEqual(y.blocks)).All(x => x);

    private static void ArtifactChecks(string povPath, string vneiPath, string? finalPath)
    {
        using var pov = ModuleDefinition.ReadModule(povPath);
        using var vnei = ModuleDefinition.ReadModule(vneiPath);
        var panel = vnei.GetType("VNEI.UI.MainVneiHandlerAuga").Fields.Single(f => f.Name == "panelData");
        Check(panel.IsPrivate && panel.FieldType.Scope.Name == "EpicLoot", "Original VNEI optional panel field explains its missing-EpicLoot whole-type scan failure.");
        var upgrade = pov.GetType("PassiveTree.Loot.ItemAscension").NestedTypes.Single(t => t.Name == "UpgradeListClean");
        MethodDefinition postfix = upgrade.Methods.Single(m => m.Name == "Postfix");
        Check(postfix.IsPrivate && postfix.IsStatic, "Original PoV postfix is private static and must be targeted through reflection.");
        Check(upgrade.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch") &&
            postfix.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPriority" && Convert.ToInt32(a.ConstructorArguments[0].Value) == 0),
            "Original recipe postfix responsibility and priority remain reviewed.");
        if (finalPath == null) return;
        using var final = ModuleDefinition.ReadModule(finalPath);
        Check(!final.AssemblyReferences.Any(r => new[] { "VNEI", "PathOfValheiman", "EpicLoot", "BetterArchery", "ExtraSlots", "SeneaLUI" }.Contains(r.Name)),
            "Final mod has no mandatory assembly reference to optional lookup targets.");
        Check(!final.CustomAttributes.Any(a => a.AttributeType.Name.Contains("IgnoresAccessChecks")), "Final mod does not introduce access-check bypasses.");
    }
}
