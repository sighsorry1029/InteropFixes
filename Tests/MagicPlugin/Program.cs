using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private const BindingFlags AllStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _checks;

    private static void Main(string[] args)
    {
        if (args.Length < 4 || args.Length > 5)
            throw new ArgumentException("Expected InteropFixes.dll, BlastedSwimmingTarredBugFix.dll, original Managed directory and BepInEx core directory.");

        string[] search = { Path.GetDirectoryName(args[0])!, args[2], args[3] };
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            string filename = new AssemblyName(request.Name).Name + ".dll";
            foreach (string directory in search)
            {
                string candidate = Path.Combine(directory, filename);
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        };

        VerifyMigrationMetadata(args[0], args[1]);
        string sandbox = Path.Combine(Path.GetTempPath(), "interop-magic-" + Guid.NewGuid().ToString("N"));
        typeof(BepInEx.Paths).GetMethod("SetExecutablePath", AllStatic)!.Invoke(null,
            new object[] { Path.Combine(sandbox, "valheim.exe"), Path.Combine(sandbox, "BepInEx"), args[3], new[] { args[2], args[3] } });
        Assembly mod = Assembly.LoadFrom(args[0]);
        Type plugin = mod.GetType("InteropFixes.InteropFixesPlugin", true)!;
        plugin.GetProperty("ModLogger", AllStatic)!.SetValue(null, new ManualLogSource("Magic migration tests"), null);
        Type module = mod.GetType("InteropFixes.MagicPluginCompatibility", true)!;
        string magicGuid = (string)plugin.GetField("MagicPluginGuid", AllStatic)!.GetRawConstantValue();
        Check(!Chainloader.PluginInfos.ContainsKey(magicGuid), "Isolated host unexpectedly contains MagicPlugin");
        MethodInfo install = module.GetMethod("TryInstall", AllStatic)!;
        ParameterInfo cosmetic = install.GetParameters()[1];
        Check(cosmetic.HasDefaultValue && Equals(cosmetic.DefaultValue, false), "Internal installer requires explicit receiver preparation; host policy is tested separately");
        Harmony absent = new("InteropFixes.Tests.AbsentMagic");
        install.Invoke(null, new object[] { absent, false });
        Check(!absent.GetPatchedMethods().Any(), "Missing MagicPlugin must not install patches");

        VerifyReplacementLifecycle(module, magicGuid);
        Type contract = mod.GetType("InteropFixes.MagicPatchContract", true)!;
        VerifyContractScope(contract);
        if (args.Length == 5)
        {
            Assembly magic = Assembly.LoadFrom(args[4]);
            MethodInfo fingerprint = contract.GetMethod("Fingerprint", AllStatic)!;
            foreach (string[] target in new[] {
                         new[] { "MagicPlugin.Patches.AttackPatch", "FireProjectileBurst_Prefix", "ProjectileContract" },
                         new[] { "MagicPlugin.Patches.PlayerPatch", "TeleportTo_Postfix", "TeleportContract" } })
            {
                MethodInfo method = magic.GetType(target[0], true)!.GetMethod(target[1], AllStatic)!;
                string actual = (string)fingerprint.Invoke(null, new object[] { method })!;
                Check(Equals(contract.GetField(target[2], AllStatic)!.GetRawConstantValue(), actual), "Known Magic contract changed: " + target[1]);
                Console.WriteLine(target[1] + "=" + actual);
            }
        }
        Console.WriteLine($"PASS: {_checks} assertions; original game DLLs loaded unchanged, real Harmony replacement/restoration exercised on managed fixtures. Unity/MagicPlugin gameplay not run.");
    }

    private static void VerifyContractScope(Type contract)
    {
        MethodInfo fingerprint = contract.GetMethod("Fingerprint", AllStatic)!;
        MethodInfo matches = contract.GetMethod("Matches", AllStatic)!;
        MethodInfo body = typeof(Program).GetMethod(nameof(ContractBody), AllStatic)!;
        string expected = (string)fingerprint.Invoke(null, new object[] { body })!;
        string baselinePath = Assembly.GetExecutingAssembly().Location;
        foreach (string mutation in new[] { "unrelated", "root", "helper" })
        {
            using AssemblyDefinition fixture = AssemblyDefinition.ReadAssembly(baselinePath);
            fixture.Name.Name = "MagicContractFixture_" + mutation + "_" + Guid.NewGuid().ToString("N");
            fixture.Name.Version = new Version(99, 98, 97, 96);
            fixture.MainModule.Mvid = Guid.NewGuid();
            TypeDefinition type = fixture.MainModule.Types.Single(t => t.Name == nameof(Program));
            MethodDefinition root = type.Methods.Single(m => m.Name == nameof(ContractBody));
            root.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
            if (mutation == "unrelated")
            {
                MethodDefinition unrelated = type.Methods.Single(m => m.Name == nameof(Original));
                unrelated.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_I4_1).OpCode = OpCodes.Ldc_I4_7;
            }
            else if (mutation == "root")
            {
                Instruction ret = root.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
                root.Body.GetILProcessor().InsertBefore(ret, Instruction.Create(OpCodes.Ldc_I4_2));
                root.Body.GetILProcessor().InsertBefore(ret, Instruction.Create(OpCodes.Add));
            }
            else
            {
                MethodDefinition helper = type.Methods.Single(m => m.Name == nameof(ContractHelper));
                helper.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_I4_1).OpCode = OpCodes.Ldc_I4_2;
            }
            string path = Path.Combine(Path.GetTempPath(), fixture.Name.Name + ".dll");
            fixture.Write(path);
            Assembly modified = Assembly.LoadFile(path);
            MethodInfo candidate = modified.GetType(nameof(Program), true)!.GetMethod(nameof(ContractBody), AllStatic)!;
            object?[] arguments = { candidate, expected, null };
            bool accepted = (bool)matches.Invoke(null, arguments)!;
            Check(accepted == (mutation == "unrelated"), "Contract mutation was misclassified: " + mutation);
        }
        MethodInfo helperMethod = typeof(Program).GetMethod(nameof(ContractHelper), AllStatic)!;
        Harmony competing = new("InteropFixes.Tests.CompetingMagicHelper");
        try
        {
            competing.Patch(helperMethod, prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(HelperPrefix), AllStatic)!));
            object?[] arguments = { body, expected, null };
            Check(!(bool)matches.Invoke(null, arguments)!, "A patched reachable helper must prevent replacement");
        }
        finally { competing.UnpatchSelf(); }
        Check(Equals(fingerprint.Invoke(null, new object[] { body }), expected), "Unpatched helper did not restore original contract");
    }

    private static void VerifyMigrationMetadata(string interopPath, string blastedPath)
    {
        using AssemblyDefinition interop = AssemblyDefinition.ReadAssembly(interopPath);
        using AssemblyDefinition blasted = AssemblyDefinition.ReadAssembly(blastedPath);
        Check(!blasted.MainModule.Types.Any(t => t.Name.StartsWith("MagicPlugin", StringComparison.Ordinal)), "Blasted still contains Magic modules");
        Check(blasted.MainModule.Types.Any(t => t.Name == "CharacterLifecycleCompatibility"), "Blasted lost generic Character protection");
        Check(!interop.MainModule.Types.Any(t => t.Name == "CharacterLifecycleCompatibility" || t.Name == "CreatureRemovalTracker"), "Interop retained generic Character runtime fixes");
        TypeDefinition magic = interop.MainModule.Types.Single(t => t.FullName == "InteropFixes.MagicPluginCompatibility");
        TypeDefinition cosmetics = interop.MainModule.Types.Single(t => t.FullName == "InteropFixes.MagicPluginCosmeticZdoPrevention");
        Check(cosmetics.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Any(i =>
            i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "BlastedSwimmingTarredBugFix_MagicCosmeticCarrier")), "Persisted cosmetic carrier marker changed");
        Check(!magic.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().Any(m =>
            m.DeclaringType.Name == "CreatureRemovalTracker" || m.DeclaringType.Name == "LiquidStuckFixPlugin"), "Magic still depends on Blasted helpers");
        Check(!interop.MainModule.AssemblyReferences.Any(r => r.Name == "BlastedSwimmingTarredBugFix" || r.Name == "MagicPlugin"), "Magic integration added a hard external DLL reference");

        MethodDefinition install = magic.Methods.Single(m => m.Name == "TryInstall");
        // The compiler emits the actual install through a closure. The bool branch and
        // default parameter prove this internal helper requires explicit preparation.
        Check(install.Parameters.Count == 2 && install.Parameters[1].ParameterType.FullName == "System.Boolean" &&
            Equals(install.Parameters[1].Constant, false), "Compiled entry lost the explicit cosmetic switch");
        Check(install.Body.Instructions.Any(i => i.OpCode.FlowControl == FlowControl.Cond_Branch), "Compiled install entry has no conditional gate");
    }

    private static void VerifyReplacementLifecycle(Type module, string magicGuid)
    {
        MethodInfo original = typeof(Program).GetMethod(nameof(Original), AllStatic)!;
        MethodInfo externalPrefix = typeof(Program).GetMethod(nameof(ExternalPrefix), AllStatic)!;
        MethodInfo replacementPrefix = typeof(Program).GetMethod(nameof(ReplacementPrefix), AllStatic)!;
        Harmony external = new(magicGuid);
        Harmony integration = new("InteropFixes.Tests.MagicLifecycle");
        try
        {
            Check(Original(1) == 2, "Fixture baseline changed");
            external.Patch(original, prefix: new HarmonyMethod(externalPrefix)
            {
                priority = Priority.High,
                before = new[] { "fixture.before" },
                after = new[] { "fixture.after" }
            });
            Check(Original(1) == 17, "External prefix did not run");
            MethodInfo patchSet = module.GetMethod("HasExpectedOwnedPatchSet", AllStatic)!;
            Check((bool)patchSet.Invoke(null, new object[] { original, HarmonyPatchType.Prefix, externalPrefix })!, "Exact original patch set rejected");
            MethodInfo extra = typeof(Program).GetMethod(nameof(HelperPrefix), AllStatic)!;
            external.Patch(original, postfix: new HarmonyMethod(extra));
            Check(!(bool)patchSet.Invoke(null, new object[] { original, HarmonyPatchType.Prefix, externalPrefix })!, "Additional Magic-owned patch was accepted");
            external.Unpatch(original, extra);
            Patch originalPatch = Harmony.GetPatchInfo(original)!.Prefixes.Single(p => p.owner == magicGuid);
            object?[] arguments = { integration, original, originalPatch, replacementPrefix, HarmonyPatchType.Prefix, null };
            bool replaced = (bool)module.GetMethod("TryReplacePatch", AllStatic)!.Invoke(null, arguments)!;
            Check(replaced, "Production replacement rejected managed fixture: " + arguments[5]);
            Check(Original(1) == 23, "Replacement prefix did not run");
            Patches inventory = Harmony.GetPatchInfo(original)!;
            Check(inventory.Prefixes.All(p => p.owner != magicGuid), "Old external prefix still active beside replacement");
            Patch replacement = inventory.Prefixes.Single(p => p.owner == integration.Id);
            Check(replacement.priority == originalPatch.priority && replacement.before.SequenceEqual(originalPatch.before) &&
                replacement.after.SequenceEqual(originalPatch.after), "Replacement lost original patch ordering");

            Type ledger = module.GetNestedType("PatchReplacement", BindingFlags.NonPublic)!;
            object record = Activator.CreateInstance(ledger,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { original, originalPatch, HarmonyPatchType.Prefix }, null)!;
            module.GetField("_projectileReplacement", AllStatic)!.SetValue(null, record);
            integration.UnpatchSelf();
            Check(!integration.GetPatchedMethods().Any(), "Replacement owner still has patches before external restoration");
            MethodInfo restore = module.GetMethod("RestoreExternalPatchesAndReset", AllStatic)!;
            Check((bool)restore.Invoke(null, null)!, "Original patch restoration failed");
            Check(Original(1) == 17, "Restored external prefix no longer runs");
            Check(module.GetField("_projectileReplacement", AllStatic)!.GetValue(null) == null, "Successful restoration kept a stale ledger");
            Check((bool)restore.Invoke(null, null)!, "Repeated shutdown restoration was not idempotent");
            Check(Harmony.GetPatchInfo(original)!.Prefixes.Count(p => p.owner == magicGuid) == 1, "Repeated restoration duplicated the external patch");
            Patch restored = Harmony.GetPatchInfo(original)!.Prefixes.Single(p => p.owner == magicGuid);
            Check(restored.priority == originalPatch.priority && restored.before.SequenceEqual(originalPatch.before) &&
                restored.after.SequenceEqual(originalPatch.after), "Restoration lost the original ordering");

            MethodInfo invalid = typeof(Program).GetMethod(nameof(InvalidPrefix), AllStatic)!;
            object?[] failureArguments = { integration, original, restored, invalid, HarmonyPatchType.Prefix, null };
            Check(!(bool)module.GetMethod("TryReplacePatch", AllStatic)!.Invoke(null, failureArguments)!,
                "Invalid replacement was accepted instead of rolling back");
            Check(Original(1) == 17, "Failed replacement did not restore original behavior");
            Check(Harmony.GetPatchInfo(original)!.Prefixes.Count(p => p.owner == magicGuid) == 1 &&
                !integration.GetPatchedMethods().Any(), "Failed replacement left duplicate or partial patches");
        }
        finally
        {
            integration.UnpatchSelf();
            external.UnpatchSelf();
        }
        Check(Original(1) == 2, "Fixture baseline not restored after cleanup");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Original(int value) => value + 1;
    private static bool ExternalPrefix(ref int __result) { __result = 17; return false; }
    private static bool ReplacementPrefix(ref int __result) { __result = 23; return false; }
    private static bool InvalidPrefix(int parameterNotPresentInOriginal) => parameterNotPresentInOriginal == 0;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ContractBody(int value) => ContractHelper(value);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ContractHelper(int value) => value + 1;
    private static void HelperPrefix() { }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        _checks++;
    }
}
