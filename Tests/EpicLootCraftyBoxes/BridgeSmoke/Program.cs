using AzuCraftyBoxes.Compatibility;
using AzuCraftyBoxes.IContainers;
using InteropFixes.Modules.EpicLootCraftyBoxes;
using Item = ItemDrop.ItemData;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }

    private static (Container Box, Item Item) Setup()
    {
        ContainerBridge.Clear();
        InteropFixesPlugin.ModLogger.Warnings.Clear();
        Player.m_localPlayer = new Player();
        EpicLootCompat.Containers.Clear();
        EpicLootCompat.OverrideItems = null;
        EpicLootCompat.SkipProviderPostfix = false;
        ContainerBridge.Initialize(typeof(EpicLootCompat).Assembly);
        Container box = new();
        Item item = new();
        box.Inventory.Items.Add(item);
        box.Seed();
        EpicLootCompat.Containers.Add(new VanillaContainer(box));
        EpicLootCompat.ObserveNow();
        return (box, item);
    }

    private static ContainerBridge.Operation Begin(Item item, bool removal = false)
    {
        Check(ContainerBridge.TryBegin(item, false, out var operation, removal) && operation != null, "Expected eligible storage operation.");
        return operation!;
    }

    private static void RejectBefore(string name, Action<Container, Item> change)
    {
        var (box, item) = Setup();
        change(box, item);
        Check(!ContainerBridge.TryBegin(item, false, out var operation), name + " must reject before materials are consumed.");
        Check(operation == null && box.Saves == 0, name + " must not save.");
        Check(InteropFixesPlugin.ModLogger.Warnings.Count > 0, name + " must explain rejection.");
    }

    private static void RejectAfter(string name, Action<Container, Item> change)
    {
        var (box, item) = Setup();
        var operation = Begin(item);
        item.Magic = "enchanted";
        change(box, item);
        int saves = box.Saves;
        ContainerBridge.Finish(operation);
        Check(box.Saves == saves, name + " must not overwrite storage.");
        Check(InteropFixesPlugin.ModLogger.Warnings.Count > 0, name + " must log failure.");
    }

    public static void Main()
    {
        var (box, item) = Setup();
        item.Magic = "unsaved";
        box.MetadataChanged();
        box.Reload();
        Check(box.Inventory.Items.Single().Magic == "plain", "Control case must reproduce discarded unsaved memory state.");

        (box, item) = Setup();
        var operation = Begin(item);
        box.SaveForMaterials();
        item.Magic = "enchanted";
        item.Durability = 100; // Later than EpicLoot's metadata event.
        ContainerBridge.Finish(operation);
        Check(box.Saves == 2, "Final save must follow material save.");
        box.MetadataChanged(); box.Reload();
        Check(box.Inventory.Items.Single().Magic == "enchanted", "Upgrade must survive reopen.");
        Check(box.Inventory.Items.Single().Durability == 100, "Late durability must be persisted.");
        box.MetadataChanged(); box.Reload();
        Check(box.Inventory.Items.Single().Magic == "enchanted", "Upgrade must survive second reopen.");

        RejectBefore("non-owner", (b, _) => b.View.Owner = false);
        RejectBefore("locally open", (b, _) => b.Busy = true);
        RejectBefore("remote in-use", (b, _) => b.View.Zdo.InUse = 1);
        RejectBefore("denied access", (b, _) => b.Access = false);
        RejectBefore("out of range", (b, _) => b.transform.position = new UnityEngine.Vector3(25, 0, 0));
        RejectBefore("invalid network view", (b, _) => b.View.Valid = false);
        RejectBefore("loading inventory", (b, _) => Container.LoadingRef(b) = true);
        RejectBefore("stale selection after reload", (b, _) => b.Reload());
        RejectBefore("removed target", (b, i) => b.Inventory.Items.Remove(i));
        RejectBefore("provider disabled", (_, _) => EpicLootCompat.OverrideItems = []);
        RejectBefore("external items update", (b, _) => b.View.Zdo.Items![1] ^= 1);
        RejectBefore("missing authoritative item bytes", (b, _) => b.View.Zdo.Items = null);
        RejectBefore("unsupported wrapper", (b, _) =>
        {
            EpicLootCompat.Containers.Clear();
            EpicLootCompat.Containers.Add(new UnsupportedContainer(b.Inventory));
        });
        RejectBefore("unknown provider item", (_, i) =>
        {
            EpicLootCompat.Containers.Clear();
            EpicLootCompat.OverrideItems = [i];
        });

        (box, item) = Setup();
        box.MetadataChanged();
        operation = Begin(item);
        item.Magic = "safe after metadata only";
        ContainerBridge.Finish(operation);
        Check(box.Saves == 1, "Use-state revision without changed item bytes must not reject.");

        (box, item) = Setup();
        ContainerBridge.Clear();
        EpicLootCompat.SkipProviderPostfix = true;
        box.SkipSavePostfix = true;
        operation = Begin(item);
        item.Magic = "cached delegates";
        ContainerBridge.Finish(operation);
        operation = Begin(item);
        item.Magic = "cached delegates again";
        ContainerBridge.Finish(operation);
        box.Reload();
        Check(box.Inventory.Items.Single().Magic == "cached delegates again", "Explicit observe/record must work if cached delegates bypass postfixes.");

        (box, item) = Setup();
        box.Inventory.Items.Remove(item);
        Player.m_localPlayer.Inventory.Items.Add(item);
        Check(ContainerBridge.TryBegin(item, false, out operation) && operation == null, "Player inventory must remain vanilla.");
        Item unrelated = new();
        Check(ContainerBridge.TryBegin(unrelated, false, out operation) && operation == null, "Other provider must remain untouched.");
        Check(ContainerBridge.TryBegin(null, false, out operation) && operation == null, "No selected item must remain untouched.");

        (box, item) = Setup();
        ContainerBridge.TrackingFailed = true;
        Check(!ContainerBridge.TryBegin(item, false, out operation) && operation == null, "Tracking failure must fail closed for storage.");
        Check(!ContainerBridge.TryBegin(new Item(), false, out operation), "Unknown external item must be blocked when classification has failed.");
        Player.m_localPlayer.Inventory.Items.Add(item);
        Check(ContainerBridge.TryBegin(item, false, out operation) && operation == null, "Player inventory remains usable after tracking failure.");
        ContainerBridge.Clear();
        Check(!ContainerBridge.TrackingFailed, "Cleanup resets tracking failure.");

        RejectAfter("ownership lost", (b, _) => b.View.Owner = false);
        RejectAfter("ownership changed away and back", (b, _) => b.View.Zdo.OwnerRevision++);
        RejectAfter("ZDO replaced", (b, _) => b.View.Zdo = new ZDO());
        RejectAfter("inventory replaced", (b, _) => b.Inventory = new Inventory());
        RejectAfter("reload during action", (b, _) => b.Reload());
        RejectAfter("external bytes during action", (b, _) => b.View.Zdo.Items![1] ^= 1);
        RejectAfter("item removed without Rune operation", (b, i) => { b.Inventory.Items.Remove(i); b.SaveForMaterials(); });
        RejectAfter("save exception", (b, _) => b.ThrowOnSave = true);

        (box, item) = Setup();
        operation = Begin(item, true);
        box.Inventory.Items.Remove(item);
        ContainerBridge.Finish(operation);
        Check(box.Saves == 0 && InteropFixesPlugin.ModLogger.Warnings.Count > 0, "Rune removal must require successful provider save.");
        (box, item) = Setup();
        operation = Begin(item, true);
        box.Inventory.Items.Remove(item);
        box.SaveForMaterials();
        ContainerBridge.Finish(operation);
        Check(box.Saves == 1 && InteropFixesPlugin.ModLogger.Warnings.Count == 0 && box.Inventory.Items.Count == 0, "Saved Rune destruction must not reinsert or duplicate equipment.");

        (box, item) = Setup();
        box.View.Owner = false;
        Check(!ContainerBridge.TryBegin(item, true, out operation), "Delayed augment completion must recheck ownership.");
        Check(InteropFixesPlugin.ModLogger.Warnings.Last().Contains("already-paid augment cost is not refunded"), "Delayed completion must not promise refund.");

        (box, item) = Setup();
        EpicLootCompat.Containers.Clear();
        Check(!ContainerBridge.TryBegin(item, false, out operation), "Tracked stale reference remains guarded.");
        ContainerBridge.Clear();
        Check(ContainerBridge.TryBegin(item, false, out operation) && operation == null, "Session cleanup must release source classifications.");

        Check(ContainerBridge.SameBytes(null, null), "Null equality");
        Check(!ContainerBridge.SameBytes(null, []), "Null versus empty");
        Check(ContainerBridge.SameBytes([1, 2], [1, 2]), "Content equality");
        Check(!ContainerBridge.SameBytes([1, 2], [1, 3]), "Content difference");
        ModuleChecks();
        Console.WriteLine($"PASS: {_checks} assertions against linked production ContainerBridge.cs. Simulation only; no Unity/game/network execution.");
    }

    private static void ModuleChecks()
    {
        var host = new HarmonyLib.Harmony("test.bridge");
        Check(!EpicLootCraftyBoxesModule.TryInstall(host), "Absent optional plugins must skip only the bridge.");
        Check(host.UnpatchCount == 0, "Absent plugins must not mutate Harmony state.");
        BepInEx.Bootstrap.Chainloader.PluginInfos[EpicLootCraftyBoxesModule.EpicLootGuid] = new(new object());
        BepInEx.Bootstrap.Chainloader.PluginInfos[EpicLootCraftyBoxesModule.CraftyBoxesGuid] = new(new object());
        Check(!EpicLootCraftyBoxesModule.TryInstall(host), "Unsupported loaded plugin contracts must fail closed before applying hooks.");
        Check(host.UnpatchCount == 1, "Failed installation must roll back its module owner.");
        BepInEx.Bootstrap.Chainloader.PluginInfos.Clear();

        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        Type module = typeof(EpicLootCraftyBoxesModule);
        var before = module.GetMethod("BeforeAugment", flags)!;
        var finalizer = module.GetMethod("AfterAction", flags)!;
        object?[] skipped = [new Item(), false, null];
        Check((bool)before.Invoke(null, skipped)! && skipped[2] == null, "An earlier prefix skip must not start a transaction.");

        var (box, item) = Setup();
        var operation = Begin(item);
        var failure = new IOException("original action failed");
        module.GetField("_active", flags)!.SetValue(null, true);
        Check(ReferenceEquals(finalizer.Invoke(null, [failure, operation]), failure), "Finalizer must preserve the exact original exception.");
        Check(box.Saves == 0, "An original action exception must not persist a partial result.");
        module.GetField("_harmony", flags)!.SetValue(null, host);
        object?[] external = [item, true, null];
        Check(!(bool)before.Invoke(null, external)!, "Partial/missing live hook coverage must reject storage before an action.");
        Player.m_localPlayer.Inventory.Items.Add(item);
        object?[] player = [item, true, null];
        Check((bool)before.Invoke(null, player)! && player[2] == null, "Missing bridge hooks must not block ordinary player inventory actions.");

        var unrelated = new HarmonyLib.Harmony("test.unrelated");
        unrelated.Patch(before, prefix: new HarmonyLib.HarmonyMethod(module, "AfterStop"));
        module.GetField("_actions", flags)!.SetValue(null, new[] { before });
        Check((bool)module.GetMethod("HasCompetingMutators", flags)!.Invoke(null, null)!,
            "A foreign action prefix must invalidate the claimed bridge contract.");
        EpicLootCraftyBoxesModule.Stop();
        Check(HarmonyLib.Harmony.GetPatchInfo(before)!.Prefixes.Any(p => p.owner == unrelated.Id), "Cleanup must preserve another module's patches.");
        Check(module.GetField("_harmony", flags)!.GetValue(null) == null && !ContainerBridge.TrackingFailed,
            "Cleanup must release the owner and storage failure state.");
        Check(module.GetField("_actions", flags)!.GetValue(null) is System.Reflection.MethodInfo[] { Length: 0 },
            "Cleanup must release cached external action methods.");

        module.GetField("_harmony", flags)!.SetValue(null, host);
        module.GetField("_active", flags)!.SetValue(null, true);
        host.ThrowOnUnpatch = true;
        EpicLootCraftyBoxesModule.Stop();
        object?[] stopped = [new Item(), true, null];
        Check((bool)before.Invoke(null, stopped)! && stopped[2] == null,
            "A failed rollback must leave surviving guards inert without blocking base game actions.");
        Check(!EpicLootCraftyBoxesModule.TryInstall(host), "A failed teardown must not be mistaken for an active installation.");
        host.ThrowOnUnpatch = false;
        EpicLootCraftyBoxesModule.Stop();
        Check(module.GetField("_harmony", flags)!.GetValue(null) == null, "A subsequent safe cleanup must release retained ownership.");
    }
}

