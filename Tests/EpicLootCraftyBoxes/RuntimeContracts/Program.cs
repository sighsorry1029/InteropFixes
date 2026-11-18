using System;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class Program
{
    private static string[] _directories = Array.Empty<string>();

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 4) throw new ArgumentException("Arguments: InteropFixes.dll EpicLoot.dll AzuCraftyBoxes.dll game-root");
            _directories = new[] { Path.GetDirectoryName(args[0])!, Path.GetDirectoryName(args[1])!, Path.GetDirectoryName(args[2])!,
                Path.Combine(args[3], "valheim_Data", "Managed"), Path.Combine(args[3], "BepInEx", "core") };
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            Assembly plugin = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Assembly epic = Assembly.LoadFrom(Path.GetFullPath(args[1]));
            Assembly crafty = Assembly.LoadFrom(Path.GetFullPath(args[2]));
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            Type bridge = plugin.GetType("InteropFixes.Modules.EpicLootCraftyBoxes.ContainerBridge", true)!;
            Type module = plugin.GetType("InteropFixes.Modules.EpicLootCraftyBoxes.EpicLootCraftyBoxesModule", true)!;
            Type contracts = plugin.GetType("InteropFixes.Modules.EpicLootCraftyBoxes.BridgeContracts", true)!;
            MethodInfo getItems = crafty.GetType("AzuCraftyBoxes.Compatibility.EpicLootCompat", true)!.GetMethod("GetItems", statics)!;
            bridge.GetField("GetItemsMethod", statics)!.SetValue(null, getItems);
            module.GetMethod("ResolveActions", statics)!.Invoke(null, new object[] { epic });
            module.GetMethod("ValidateProvider", statics)!.Invoke(null, new object[] { crafty });
            Assembly game = Assembly.LoadFrom(Path.Combine(_directories[3], "assembly_valheim.dll"));
            Type container = game.GetType("Container", true)!;
            MethodInfo calls = contracts.GetMethod("RequireCalls", statics)!;
            calls.Invoke(null, new object[] { container.GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!, "Inventory", "Save" });
            calls.Invoke(null, new object[] { container.GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance)!, "Inventory", "Load" });
            MethodInfo[] actions = (MethodInfo[])module.GetField("_actions", statics)!.GetValue(null)!;
            if (actions.Length != 5 || module.GetField("_selectedItem", statics)!.GetValue(null) == null ||
                module.GetField("_cancel", statics)!.GetValue(null) == null)
                throw new InvalidOperationException("Expected cached action/selection/cancel bindings.");
            Console.WriteLine("PASS: Mono original-DLL binding, compiled UI delegates and relevant provider/game IL contracts. No Unity object construction, plugin startup, gameplay, Harmony dispatch or network execution.");
            Console.WriteLine("Optional assembly references: " + string.Join(", ", plugin.GetReferencedAssemblies()
                .Where(a => a.Name == "EpicLoot" || a.Name == "AzuCraftyBoxes").Select(a => a.Name)));
            if (plugin.GetReferencedAssemblies().Any(a => a.Name == "EpicLoot" || a.Name == "AzuCraftyBoxes"))
                throw new InvalidOperationException("Optional integration must not produce a hard assembly reference.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static Assembly? Resolve(object sender, ResolveEventArgs args)
    {
        string file = new AssemblyName(args.Name).Name + ".dll";
        foreach (string directory in _directories)
        {
            string path = Path.Combine(directory, file);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
}
