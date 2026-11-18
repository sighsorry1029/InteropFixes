using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mono.Cecil;
using VNEI.Logic;
using InteropFixes.Modules.Vnei;
using UnityEngine;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message + "\n" + string.Join("\n", InteropFixes.InteropFixesPlugin.ModLogger.Messages));
        _checks++;
    }

    private static void Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 3) throw new ArgumentException("Usage: <VNEI.dll> <original Managed> [InteropFixes.dll]");
        RunOptionalChecks();
        RunLookupChecks();
        RunIlChecks(args[0]);
        RunContracts(args[1], args.Length == 3 ? args[2] : null);
        Console.WriteLine($"{_checks} checks passed. Unity boundaries were simulated; no game session was run.");
    }

    private static void RunLookupChecks()
    {
        var hammer = new Item("Hammer");
        var wood = new Item("Wood");
        var fallback = new Item("Native fallback");
        Indexing.Items["Hammer"] = Indexing.Items["$item_hammer"] = hammer;
        Indexing.Items["Wood"] = wood;
        Indexing.Items["fallback"] = fallback;
        var item = new ItemDrop.ItemData { m_dropPrefab = new GameObject { name = "Hammer" }, m_gridPos = new Vector2i(2, 4) };
        item.m_shared.m_name = "$item_hammer";
        var grid = new InventoryGrid(); grid.Inventory.Items[(2, 4)] = item;
        var tooltip = new UITooltip { m_topic = "<color=purple>Wood</color>", m_text = "Epic enchantment description" };
        UITooltip.SetCurrent(tooltip);
        HoveredItemLookup.Initialize(name => Indexing.GetItem(name));
        HoveredItemLookup.Enable();
        HoveredItemLookup.Capture(grid, item, tooltip);
        Check(ReferenceEquals(HoveredItemLookup.Resolve(tooltip.m_topic), hammer), "Generated magic name resolves actual prefab");
        Check(ReferenceEquals(HoveredItemLookup.Resolve("Wood"), hammer), "Identity wins over a display name colliding with another item");
        Check(tooltip.m_topic == "<color=purple>Wood</color>" && tooltip.m_text == "Epic enchantment description", "Display data is unchanged");
        for (int i = 0; i < 10; i++) HoveredItemLookup.Capture(grid, item, tooltip);
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), hammer), "Repeated tooltip refresh keeps same identity");
        item.m_dropPrefab = null;
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), hammer), "Missing prefab falls back to shared token");
        item.m_dropPrefab = new GameObject { name = "Unindexed" };
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), hammer), "Unindexed prefab falls back to shared token");
        item.m_shared.m_name = "$unknown";
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "Unknown identity preserves native lookup");
        Check(HoveredItemLookup.Resolve("Missing") == null, "Absent indexed item is not fabricated");
        item.m_shared.m_name = "$item_hammer"; item.m_dropPrefab.name = "Hammer";

        var rect = (RectTransform)tooltip.transform;
        rect.ContainsPointer = false;
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "EpicLoot's stale m_current after pointer exit is rejected");
        rect.ContainsPointer = true;
        tooltip.isActiveAndEnabled = false;
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "Hidden tooltip cannot supply identity");
        tooltip.isActiveAndEnabled = true; grid.isActiveAndEnabled = false;
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "Inactive source grid cannot supply identity");
        grid.isActiveAndEnabled = true;
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), hammer), "Reopening cached equipment tooltip preserves binding");

        item.m_gridPos = new Vector2i(3, 4);
        grid.Inventory.Items.Remove((2, 4)); grid.Inventory.Items[(3, 4)] = item;
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "Moving item invalidates old cell even while still in inventory");
        item.m_gridPos = new Vector2i(2, 4); grid.Inventory.Items[(2, 4)] = new ItemDrop.ItemData();
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "Reused slot rejects previous ItemData reference");
        grid.Inventory.Items[(2, 4)] = item;
        Inventory inventory = grid.Inventory; grid.Inventory = new Inventory(); grid.Inventory.Items[(2, 4)] = item;
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "Another open chest cannot reuse previous inventory binding");
        grid.Inventory = inventory;
        HoveredItemLookup.Invalidate(tooltip);
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "Set repurposing invalidates binding");
        HoveredItemLookup.Capture(grid, item, tooltip);
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), hammer), "Postfix recaptures after Set even when original Create was skipped");
        UITooltip.SetCurrent(new UITooltip());
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "Unrelated crafting/VNEI tooltip follows native path");
        UITooltip.SetCurrent(null);
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "Missing current tooltip follows native path");
        UITooltip.SetCurrent(tooltip);
        tooltip.Canvas = new Canvas { renderMode = RenderMode.ScreenSpaceCamera, worldCamera = new Camera() };
        HoveredItemLookup.Resolve("fallback");
        Check(ReferenceEquals(RectTransformUtility.LastCamera, tooltip.Canvas.worldCamera), "Camera-space UI uses its own camera for hover test");
        tooltip.Canvas.renderMode = RenderMode.ScreenSpaceOverlay; HoveredItemLookup.Resolve("fallback");
        Check(RectTransformUtility.LastCamera == null, "Overlay UI uses no camera");
        ZInput.Mouse = false; ZInput.Gamepad = true;
        tooltip.m_gamepadFocusObject = new GameObject();
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), hammer), "Focused extra slot works outside grid mouse bounds");
        tooltip.m_gamepadFocusObject.activeInHierarchy = false;
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "Inactive gamepad focus rejects stale identity");
        tooltip.m_gamepadFocusObject = null; grid.Selected = item;
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), hammer), "Native gamepad selected item fallback");
        grid.Selected = null;
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "Different gamepad selection is rejected");
        ZInput.Gamepad = false; ZInput.Touch = true; tooltip.m_gamepadFocusObject = new GameObject();
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), hammer), "Touch focus uses existing native selection");
        ZInput.Touch = false; ZInput.Mouse = true;
        HoveredItemLookup.Disable();
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "Residual wrapper preserves native lookup during incomplete teardown");
        HoveredItemLookup.Clear(); HoveredItemLookup.Initialize(name => Indexing.GetItem(name)); HoveredItemLookup.Enable();
        Check(ReferenceEquals(HoveredItemLookup.Resolve("fallback"), fallback), "Teardown clears session bindings");
    }

    private static void RunOptionalChecks()
    {
        var harmony = new Harmony("InteropFixes.Vnei.Tests");
        var registry = BepInEx.Bootstrap.Chainloader.PluginInfos;
        Check(!VneiItemLookupModule.TryInstall(harmony), "Missing optional VNEI does not install patches");
        registry[VneiItemLookupModule.VneiGuid] = new BepInEx.Bootstrap.PluginInfo { Instance = new VNEI.Plugin() };
        UnityEngine.Application.isBatchMode = true;
        Check(!VneiItemLookupModule.TryInstall(harmony), "Dedicated server skips client tooltip integration");
        UnityEngine.Application.isBatchMode = false;
        UnityEngine.SystemInfo.graphicsDeviceType = UnityEngine.Rendering.GraphicsDeviceType.Null;
        Check(!VneiItemLookupModule.TryInstall(harmony), "Null graphics device skips client tooltip integration");
        UnityEngine.SystemInfo.graphicsDeviceType = UnityEngine.Rendering.GraphicsDeviceType.Direct3D11;
        registry[VneiItemLookupModule.LegacyGuid] = new BepInEx.Bootstrap.PluginInfo();
        Check(!VneiItemLookupModule.TryInstall(harmony), "Legacy standalone owner suppresses only this integration");
        registry.Remove(VneiItemLookupModule.LegacyGuid);
        MethodInfo update = AccessTools.DeclaredMethod(typeof(VNEI.Plugin), "Update");
        var foreign = new Harmony("InteropFixes.Vnei.Tests.Foreign");
        foreign.Patch(update, prefix: new HarmonyMethod(typeof(Program), nameof(ForeignPrefix)),
            transpiler: new HarmonyMethod(typeof(Program), nameof(ForeignLookupChange)));
        try
        {
            Check(!VneiItemLookupModule.TryInstall(harmony), "Unrecognized VNEI flow declines integration");
            var patches = Harmony.GetPatchInfo(update);
            Check(patches != null && patches.Owners.Contains(foreign.Id) && !patches.Owners.Contains(harmony.Id),
                "Failed installation removes only its own partial patch; other owner survives");
            VneiItemLookupModule.Stop();
            Check(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m).Owners.Contains(harmony.Id)), "Repeated Stop leaves no integration hooks");
        }
        finally { foreign.UnpatchSelf(); }
        try
        {
            Check(VneiItemLookupModule.TryInstall(harmony), "Compatible optional module installs all hooks");
            Check(VneiItemLookupModule.TryInstall(harmony), "Repeated successful install is idempotent");
            Check(Harmony.GetAllPatchedMethods().Count(m => Harmony.GetPatchInfo(m).Owners.Contains(harmony.Id)) == 3,
                "Complete installation owns exactly three targets");
            var actual = new Item("Actual"); var display = new Item("Displayed");
            Indexing.Items["Actual"] = actual; Indexing.Items["Displayed"] = display;
            var item = new ItemDrop.ItemData { m_dropPrefab = new GameObject { name = "Actual" } };
            var grid = new InventoryGrid(); grid.Inventory.Items[(0, 0)] = item;
            var tooltip = new UITooltip { m_topic = "Displayed" };
            UITooltip.SetCurrent(tooltip);
            HoveredItemLookup.Capture(grid, item, tooltip);
            ((VNEI.Plugin)registry[VneiItemLookupModule.VneiGuid].Instance!).RunUpdate();
            Check(ReferenceEquals(VNEI.Plugin.Shown, actual), "Patched runtime calls resolver and casts to VNEI item");
            tooltip.Set("Displayed", "", null!, default);
            ((VNEI.Plugin)registry[VneiItemLookupModule.VneiGuid].Instance!).RunUpdate();
            Check(ReferenceEquals(VNEI.Plugin.Shown, display), "Patched Set invalidates stale identity before fallback");
            var blockedUnpatch = new Harmony("InteropFixes.Vnei.Tests.BlockUnpatch");
            blockedUnpatch.Patch(AccessTools.DeclaredMethod(typeof(Harmony), "Unpatch", new[] { typeof(MethodBase), typeof(MethodInfo) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(BlockOwnUnpatch)));
            try
            {
                HoveredItemLookup.Capture(grid, item, tooltip);
                VneiItemLookupModule.Stop();
                Check(Harmony.GetPatchInfo(update).Owners.Contains(harmony.Id), "Simulated unsuccessful removal leaves own wrapper installed");
                ((VNEI.Plugin)registry[VneiItemLookupModule.VneiGuid].Instance!).RunUpdate();
                Check(ReferenceEquals(VNEI.Plugin.Shown, display), "Partial teardown disables identity but retains callable native lookup");
                Check(!VneiItemLookupModule.TryInstall(harmony), "Residual patch ledger prevents incompatible adapter reinitialization");
            }
            finally { blockedUnpatch.UnpatchSelf(); }
            VneiItemLookupModule.Stop();
            Check(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m).Owners.Contains(harmony.Id)), "Successful teardown removes all three hooks");
        }
        finally { VneiItemLookupModule.Stop(); registry.Clear(); Indexing.Items.Clear(); }
    }

    private static void ForeignPrefix() { }

    private static bool BlockOwnUnpatch(Harmony __instance) => __instance.Id != "InteropFixes.Vnei.Tests";

    private static IEnumerable<CodeInstruction> ForeignLookupChange(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo lookup = typeof(Indexing).GetMethod("GetItem")!;
        MethodInfo replacement = AccessTools.DeclaredMethod(typeof(Program), nameof(OtherLookup));
        foreach (CodeInstruction instruction in instructions)
        {
            var copy = new CodeInstruction(instruction);
            if (copy.Calls(lookup)) copy.operand = replacement;
            yield return copy;
        }
    }

    private static Item? OtherLookup(string name) => Indexing.GetItem(name);

    private static void RunIlChecks(string vneiPath)
    {
        using ModuleDefinition module = ModuleDefinition.ReadModule(vneiPath);
        MethodDefinition update = module.Types.Single(t => t.FullName == "VNEI.Plugin").Methods.Single(m => m.Name == "Update");
        MethodInfo lookup = typeof(Indexing).GetMethod(nameof(Indexing.GetItem))!;
        RecipeLookupPatch.Initialize(typeof(VNEI.Plugin), lookup);
        var ops = typeof(System.Reflection.Emit.OpCodes).GetFields().Where(f => f.FieldType == typeof(System.Reflection.Emit.OpCode))
            .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);
        var code = update.Body.Instructions.Select(i =>
        {
            object? operand = i.Operand;
            if (operand is MethodReference m && m.FullName == "VNEI.Logic.Item VNEI.Logic.Indexing::GetItem(System.String)") operand = lookup;
            else if (operand is MethodReference trim && trim.DeclaringType.FullName == "System.String" && trim.Name == "Trim" && trim.Parameters.Count == 0)
                operand = typeof(string).GetMethod("Trim", Type.EmptyTypes);
            else if (operand is MethodReference replace && replace.DeclaringType.FullName == "System.Text.RegularExpressions.Regex" && replace.Name == "Replace" && replace.Parameters.Count == 2)
                operand = typeof(System.Text.RegularExpressions.Regex).GetMethod("Replace", new[] { typeof(string), typeof(string) });
            else if (operand is MethodReference show && show.DeclaringType.FullName == "VNEI.UI.BaseUI" && show.Name == "ShowRecipe")
                operand = typeof(VNEI.UI.BaseUI).GetMethod("ShowRecipe");
            else if (operand is FieldReference f && f.DeclaringType.FullName == "UITooltip") operand = AccessTools.DeclaredField(typeof(UITooltip), f.Name);
            else if (operand is FieldReference p && p.DeclaringType.FullName == "VNEI.Plugin" && (p.Name == "viewRecipeHotkey" || p.Name == "RichTagRegex"))
                operand = AccessTools.DeclaredField(typeof(VNEI.Plugin), p.Name);
            else if (operand is FieldReference empty && empty.DeclaringType.FullName == "System.String" && empty.Name == "Empty")
                operand = typeof(string).GetField("Empty");
            else if (operand is Mono.Cecil.Cil.VariableDefinition variable) operand = variable.Index;
            return new CodeInstruction(ops[i.OpCode.Value], operand);
        }).ToList();
        var matched = code.Where(i => i.Calls(lookup)).ToList();
        Check(matched.Count == 3, "Provided VNEI DLL has exactly three tooltip lookups");
        var generator = new DynamicMethod("Labels", typeof(void), Type.EmptyTypes).GetILGenerator();
        matched[0].labels.Add(generator.DefineLabel());
        matched[0].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        matched[2].blocks.Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
        var result = RecipeLookupPatch.Transpiler(code).ToList();
        Check(RecipeLookupPatch.Ready && result.Count == code.Count + 3, "Original VNEI tooltip flow accepted with three typed casts");
        int replaced = 0;
        int output = 0;
        for (int i = 0; i < code.Count; i++)
        {
            bool isLookup = code[i].Calls(lookup);
            if (isLookup) replaced++;
            Check(result[output].opcode == code[i].opcode && (isLookup
                ? result[output].operand is MethodInfo m && m.DeclaringType == typeof(HoveredItemLookup) && m.Name == "Resolve"
                : Equals(result[output].operand, code[i].operand)), "Every non-lookup instruction/operand preserved: " + i);
            Check(result[output].labels.SequenceEqual(code[i].labels), "Branch labels preserved: " + i);
            if (isLookup)
            {
                Check(result[output + 1].opcode == OpCodes.Castclass && Equals(result[output + 1].operand, typeof(Item)), "Original VNEI item stack type restored");
                Check(result[output].blocks.Concat(result[output + 1].blocks).SequenceEqual(code[i].blocks), "Exception boundaries preserved across call and cast");
                output++;
            }
            else Check(result[output].blocks.SequenceEqual(code[i].blocks), "Non-call exception boundaries preserved: " + i);
            output++;
        }
        Check(replaced == 3 && code.Count(i => i.Calls(lookup)) == 3, "All three call replacements are atomic and source IL remains unchanged");
        foreach (var incompatible in new[]
        {
            code.Where(i => !ReferenceEquals(i, matched[0])).ToList(),
            code.Where(i => !(i.operand is FieldInfo f && f.Name == "m_topic")).ToList(),
            code.Where(i => !(i.operand is FieldInfo f && f.Name == "RichTagRegex")).ToList(),
            code.Where(i => !(i.operand is MethodInfo m && m.Name == "ShowRecipe")).ToList(),
            ChangeLookupInput(code, matched[1])
        })
        {
            var unchanged = RecipeLookupPatch.Transpiler(incompatible).ToList();
            Check(!RecipeLookupPatch.Ready && unchanged.SequenceEqual(incompatible), "Unrecognized layout fails closed without partial changes");
        }
        // An unrelated rebuild/addition before this flow must not disable integration.
        var unrelated = new List<CodeInstruction> { new(OpCodes.Nop) }; unrelated.AddRange(code);
        RecipeLookupPatch.Transpiler(unrelated).ToList();
        Check(RecipeLookupPatch.Ready, "Unrelated instruction additions are allowed without version/hash gating");
        RecipeLookupPatch.Clear();
    }

    private static List<CodeInstruction> ChangeLookupInput(List<CodeInstruction> code, CodeInstruction lookup)
    {
        var changed = code.Select(i => new CodeInstruction(i)).ToList();
        changed[code.IndexOf(lookup) - 1] = new CodeInstruction(OpCodes.Ldstr, "Different lookup responsibility");
        return changed;
    }

    private static void RunContracts(string managed, string? bridge)
    {
        using var game = ModuleDefinition.ReadModule(Path.Combine(managed, "assembly_valheim.dll"));
        using var ui = ModuleDefinition.ReadModule(Path.Combine(managed, "assembly_guiutils.dll"));
        var grid = game.Types.Single(t => t.Name == "InventoryGrid");
        Check(grid.Methods.Single(m => m.Name == "CreateItemTooltip").IsPrivate, "Original CreateItemTooltip remains private; Harmony target only");
        Check(grid.Methods.Single(m => m.Name == "GetInventory").IsPublic && grid.Methods.Single(m => m.Name == "GetGamepadSelectedItem").IsPublic,
            "Inventory reads use original public APIs");
        var current = ui.Types.Single(t => t.Name == "UITooltip").Fields.Single(f => f.Name == "m_current");
        Check(current.IsPrivate && current.IsStatic, "Original tooltip field is private static; cached reflection required");
        Check(ui.Types.Single(t => t.Name == "UITooltip").Methods.Any(m => m.Name == "Set" && m.Parameters.Count == 4), "Original Set overload matches patch");
        if (bridge == null) return;
        using var mod = ModuleDefinition.ReadModule(bridge);
        Check(!mod.AssemblyReferences.Any(r => r.Name is "VNEI" or "InventorySlots"), "Final bundle has no VNEI/InventorySlots assembly dependency");
        Check(!mod.CustomAttributes.Any(a => a.AttributeType.Name.Contains("IgnoresAccessChecks")), "Final bundle uses no private-access bypass attribute");
    }
}
