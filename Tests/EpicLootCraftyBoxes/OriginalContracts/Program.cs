using System.Security.Cryptography;
using Mono.Cecil;

// Read-only original metadata/IL inspection. Does not load game/mod assemblies into the CLR,
// execute plugins, publicize assemblies, or claim live Harmony/Unity compatibility.
string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string defaultGame = Path.Combine(home, ".codex", "references", "valheim", "snapshots", "client-b26249040-windows-x64-20261029T094619Z-depot-restored", "original", "valheim_Data", "Managed", "assembly_valheim.dll");
string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.kesomannen.gale", "cache");
string gamePath = args.Length > 0 ? args[0] : defaultGame;
string epicPath = args.Length > 1 ? args[1] : Path.Combine(cache, "RandyKnapp-EpicLoot", "0.14.13", "BepInEx", "plugins", "RandyKnapp-EpicLoot", "EpicLoot.dll");
string craftyPath = args.Length > 2 ? args[2] : Path.Combine(cache, "Azumatt-AzuCraftyBoxes", "1.8.28", "BepInEx", "plugins", "Azumatt-AzuCraftyBoxes", "AzuCraftyBoxes.dll");
using DefaultAssemblyResolver resolver = new();
foreach (string directory in new[] { Path.GetDirectoryName(gamePath)!, Path.GetDirectoryName(epicPath)!, Path.GetDirectoryName(craftyPath)!,
    @"C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\core" }) resolver.AddSearchDirectory(directory);
ReaderParameters parameters = new() { AssemblyResolver = resolver };
using AssemblyDefinition game = AssemblyDefinition.ReadAssembly(gamePath, parameters);
using AssemblyDefinition epic = AssemblyDefinition.ReadAssembly(epicPath, parameters);
using AssemblyDefinition crafty = AssemblyDefinition.ReadAssembly(craftyPath, parameters);
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
TypeDefinition Type(AssemblyDefinition assembly, string name)
{
    TypeDefinition? result = assembly.MainModule.GetType(name);
    Check(result != null, "Missing type: " + name);
    return result!;
}
MethodDefinition Method(TypeDefinition type, string name, string result, params string[] parameters)
{
    var found = type.Methods.Where(m => m.Name == name && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)).ToArray();
    Check(found.Length == 1 && found[0].ReturnType.FullName == result, "Method contract: " + type.FullName + "." + name);
    return found[0];
}
FieldDefinition Field(TypeDefinition type, string name, string fieldType)
{
    FieldDefinition? field = type.Fields.SingleOrDefault(f => f.Name == name);
    Check(field?.FieldType.FullName == fieldType, "Field contract: " + type.FullName + "." + name + "; available=" + string.Join(", ", type.Fields.Select(f => f.Name + ":" + f.FieldType.FullName)));
    return field!;
}
bool Calls(MethodDefinition method, string declaringType, string name) => method.HasBody && method.Body.Instructions
    .Any(i => i.Operand is MethodReference called && called.DeclaringType.FullName == declaringType && called.Name == name);

TypeDefinition container = Type(game, "Container");
Check(Field(container, "m_nview", "ZNetView").IsPrivate, "m_nview is a private field in the original game; bridge must use cached access.");
Check(Field(container, "m_loading", "System.Boolean").IsPrivate, "m_loading must be read via cached access.");
Check(Field(container, "m_lastRevision", "System.UInt32").IsPrivate, "m_lastRevision must be read via cached access.");
MethodDefinition save = Method(container, "Save", "System.Void");
MethodDefinition load = Method(container, "Load", "System.Boolean");
Check(save.IsPrivate && load.IsPrivate, "Private save/load contract must not rely on publicized references.");
Check(Calls(save, "Inventory", "Save"), "Container.Save must serialize its inventory.");
Check(Calls(load, "Inventory", "Load"), "Container.Load must load its inventory.");
Check(Method(container, "GetInventory", "Inventory").IsPublic, "GetInventory public API.");
Check(Method(container, "IsInUse", "System.Boolean").IsPublic, "IsInUse public API.");
TypeDefinition zdo = Type(game, "ZDO");
Check(Method(zdo, "get_DataRevision", "System.UInt32").IsPublic, "DataRevision public getter.");
Check(Method(zdo, "get_OwnerRevision", "System.UInt16").IsPublic, "OwnerRevision public getter.");
Method(Type(game, "ZNet"), "StopAll", "System.Void", "System.Boolean");

foreach (string panel in new[] { "EnchantUI", "AugmentUI", "DisenchantUI", "RuneUI" })
{
    TypeDefinition type = Type(epic, "EpicLoot_UnityLib." + panel);
    MethodDefinition action = Method(type, "DoMainAction", "System.Void");
    Check(!action.IsStatic && action.HasBody, "Panel action must be a concrete instance method: " + panel);
}
Method(Type(epic, "EpicLoot.CraftingV2.EnchantingUIController"), "OnAugmentComplete", "System.Void", "ItemDrop/ItemData", "System.Int32", "EpicLoot.MagicItemEffect");
Method(Type(epic, "EpicLoot_UnityLib.EnchantingTableUIPanelBase"), "Cancel", "System.Void");
TypeDefinition compat = Type(crafty, "AzuCraftyBoxes.Compatibility.EpicLootCompat");
Check(Method(compat, "GetItems", "System.Collections.Generic.List`1<ItemDrop/ItemData>").IsStatic, "CraftyBoxes items provider must be static.");
Check(Method(compat, "Nearby", "System.Collections.Generic.List`1<AzuCraftyBoxes.IContainers.IContainer>").IsStatic, "Nearby must be static.");
Check(Method(Type(crafty, "AzuCraftyBoxes.Util.Functions.MiscFunctions"), "HasAccessToContainer", "System.Boolean", "Container").IsStatic, "Access policy must be static.");
Check(Field(Type(crafty, "AzuCraftyBoxes.IContainers.VanillaContainer"), "<_container>P", "Container").IsPrivate, "Primary-constructor wrapper field uses cached reflection.");
Check(Field(Type(crafty, "AzuCraftyBoxes.AzuCraftyBoxesPlugin"), "mRange", "BepInEx.Configuration.ConfigEntry`1<System.Single>").IsStatic, "Use CraftyBoxes configured range.");

// Mirror the relevant runtime call contracts; unrelated method bodies, versions, tokens
// and MVIDs are deliberately not activation conditions.
Field(Type(epic, "EpicLoot_UnityLib.EnchantingTableUIPanelBase"), "AvailableItems", "EpicLoot_UnityLib.MultiSelectItemList");
Method(Type(epic, "EpicLoot_UnityLib.IListElement"), "GetItem", "ItemDrop/ItemData");
TypeDefinition listType = Type(epic, "EpicLoot_UnityLib.MultiSelectItemList");
MethodDefinition selection = listType.Methods.Single(m => m.Name == "GetSingleSelectedItem" && m.Parameters.Count == 0);
Check(selection.IsPublic && !selection.IsStatic && selection.GenericParameters.Count == 1 &&
      selection.ReturnType.FullName == "System.Tuple`2<T,System.Int32>", "Selected-item adapter must retain the typed tuple contract.");
string[] panels = ["EnchantUI", "AugmentUI", "DisenchantUI"];
string[] actions = ["EnchantItemAndReturnSuccessDialog", "AugmentItem", "DisenchantItem"];
for (int i = 0; i < panels.Length; i++)
    Check(Calls(Method(Type(epic, "EpicLoot_UnityLib." + panels[i]), "DoMainAction", "System.Void"),
        "EpicLoot.CraftingV2.EnchantingUIController", actions[i]), "Action workflow must still execute synchronously: " + panels[i]);
TypeDefinition rune = Type(epic, "EpicLoot_UnityLib.RuneUI");
MethodDefinition runeAction = Method(rune, "DoMainAction", "System.Void");
Check(Calls(runeAction, rune.FullName, "ExtractSelectedEnchantment") && Calls(runeAction, rune.FullName, "EtchSelectedRune"),
    "Rune action must retain both synchronous edit paths.");
Check(Calls(Method(rune, "ExtractSelectedEnchantment", "System.Boolean", "ItemDrop/ItemData", "System.Single", "System.Single"),
    "EpicLoot_UnityLib.InventoryManagement", "RemoveExactItem"), "Rune destruction must use the provider removal transaction.");
MethodDefinition augment = Method(Type(epic, "EpicLoot.CraftingV2.EnchantingUIController"), "OnAugmentComplete", "System.Void",
    "ItemDrop/ItemData", "System.Int32", "EpicLoot.MagicItemEffect");
Check(augment.IsStatic && Calls(augment, "EpicLoot.MagicItem", "ReplaceEffect"), "Delayed augment must retain the guarded effect mutation.");
MethodDefinition itemsProvider = Method(compat, "GetItems", "System.Collections.Generic.List`1<ItemDrop/ItemData>");
Check(Calls(itemsProvider, compat.FullName, "Nearby") && Calls(itemsProvider, "AzuCraftyBoxes.IContainers.IContainer", "GetInventory") &&
    Calls(itemsProvider, "Inventory", "GetAllItems"), "Provider must expose actual nearby inventory item references.");
MethodDefinition removeExact = Method(compat, "RemoveExactItem", "System.Int32", "ItemDrop/ItemData", "System.Int32");
Check(removeExact.IsStatic && Calls(removeExact, "Inventory", "RemoveItem") && Calls(removeExact, "AzuCraftyBoxes.IContainers.IContainer", "Save"),
    "Successful provider removal must persist through its wrapper.");
TypeDefinition vanilla = Type(crafty, "AzuCraftyBoxes.IContainers.VanillaContainer");
Check(Calls(Method(vanilla, "Save", "System.Void"), "Container", "Save"), "Vanilla wrapper must save the observed container.");
Check(Calls(Method(vanilla, "GetInventory", "Inventory"), "Container", "GetInventory"), "Vanilla wrapper must retain live inventory identity.");

foreach ((AssemblyDefinition assembly, string patchName) in new[] {
    (crafty, "AzuCraftyBoxes.Patches.ContainerLoadPatch"),
    (crafty, "AzuCraftyBoxes.Patches.ContainerSaveInvalidateCountsPatch"),
    (epic, "EpicLoot.Container_Load_Patch") })
{
    TypeDefinition patch = Type(assembly, patchName);
    Check(patch.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"), "Reviewed container observer must declare its Harmony target.");
    Check(patch.Methods.Any(m => m.Name == "Postfix") && !patch.Methods.Any(m => m.Name is "Prefix" or "Transpiler"),
        "Reviewed pair must not self-reject the competing mutation guard: " + patchName);
}

IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (TypeDefinition type in types)
    {
        yield return type;
        foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
    }
}
HashSet<string> guardedTypes = ["Container", "EpicLoot_UnityLib.EnchantUI", "EpicLoot_UnityLib.AugmentUI",
    "EpicLoot_UnityLib.DisenchantUI", "EpicLoot_UnityLib.RuneUI", "EpicLoot.CraftingV2.EnchantingUIController",
    "AzuCraftyBoxes.Compatibility.EpicLootCompat", "AzuCraftyBoxes.IContainers.VanillaContainer",
    "AzuCraftyBoxes.Util.Functions.MiscFunctions"];
HashSet<string> guardedNames = ["Save", "Load", "DoMainAction", "OnAugmentComplete", "GetItems", "Nearby",
    "RemoveExactItem", "HasAccessToContainer"];
foreach (AssemblyDefinition assembly in new[] { epic, crafty })
{
    foreach (TypeDefinition type in AllTypes(assembly.MainModule.Types))
    {
        foreach (CustomAttribute target in type.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
        {
            string? targetType = target.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().FirstOrDefault()?.FullName;
            string? targetName = target.ConstructorArguments.Select(a => a.Value).OfType<string>().FirstOrDefault();
            if (targetType == null || targetName == null || !guardedTypes.Contains(targetType) || !guardedNames.Contains(targetName)) continue;
            string[] hooks = type.Methods.Where(m => m.Name is "Prefix" or "Postfix" or "Transpiler" or "Finalizer").Select(m => m.Name).ToArray();
            Check(!hooks.Contains("Prefix") && !hooks.Contains("Transpiler"), "Reviewed optional mods have a competing mutation: " + type.FullName);
            Console.WriteLine("Reviewed compatible observer: " + type.FullName + " -> " + targetType + "." + targetName + " [" + string.Join(",", hooks) + "]");
        }
        foreach (MethodDefinition method in type.Methods.Where(m => Calls(m, "HarmonyLib.Harmony", "Patch")))
            Console.WriteLine("Manual Harmony.Patch site for review: " + method.FullName);
    }
}

Console.WriteLine($"PASS: {checks} original DLL shape/IL contracts. Metadata only; no game execution.");
foreach (string path in new[] { gamePath, epicPath, craftyPath })
{
    using FileStream stream = File.OpenRead(path);
    Console.WriteLine($"{Path.GetFileName(path)} SHA256 {Convert.ToHexString(SHA256.HashData(stream))}");
}

