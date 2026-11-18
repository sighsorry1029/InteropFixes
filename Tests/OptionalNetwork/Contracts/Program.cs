using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 3) throw new ArgumentException("final InteropFixes.dll, original client assembly_valheim.dll, original server assembly_valheim.dll required");
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
{
    foreach (TypeDefinition type in types)
    {
        yield return type;
        foreach (TypeDefinition nested in Types(type.NestedTypes)) yield return nested;
    }
}
bool Calls(MethodDefinition method, string type, string name) => method.HasBody && method.Body.Instructions.Any(i =>
    i.Operand is MethodReference called && called.DeclaringType.FullName == type && called.Name == name);

using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory("C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/core");
resolver.AddSearchDirectory(Path.GetDirectoryName(args[1]));
using AssemblyDefinition artifact = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
TypeDefinition[] types = Types(artifact.MainModule.Types).ToArray();
MethodDefinition[] methods = types.SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
Instruction[] instructions = methods.SelectMany(m => m.Body.Instructions).ToArray();
TypeDefinition plugin = artifact.MainModule.GetType("InteropFixes.InteropFixesPlugin");
TypeDefinition coordinator = artifact.MainModule.GetType("InteropFixes.CosmeticNetworkCoordinator");
Check(!artifact.MainModule.AssemblyReferences.Any(r => r.Name == "ServerSync"), "Optional installation must not reference a standalone ServerSync assembly.");
Check(!types.Any(t => t.FullName.StartsWith("ServerSync.", StringComparison.Ordinal)), "Final artifact must not contain merged ServerSync types.");
Check(!artifact.MainModule.GetTypeReferences().Any(t => t.Namespace == "ServerSync"), "No latent ServerSync type references may remain.");
Check(!plugin.Fields.Any(f => f.Name == "SyncedConfig") && !plugin.Methods.Any(m => m.Name == "get_CosmeticSessionReady"), "Mandatory synchronization gates must be removed.");
Check(!methods.Any(m => Calls(m, "BepInEx.Configuration.ConfigFile", "Bind")), "The plugin must not bind user configuration settings.");
Check(!plugin.Fields.Any(f => f.FieldType.FullName.StartsWith("BepInEx.Configuration.ConfigEntry", StringComparison.Ordinal)), "No retired option may remain cached by the plugin.");
Check(!instructions.Any(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "EnableNetworkCosmeticOptimization")), "Old false configuration values must not be consulted.");
Check(!methods.Any(m => m.Name is "AfterInitialSync" or "RPC_FromServerConfigSync" or "RegisterAndCheckVersion" or "DisconnectClient"), "Required synchronization/version rejection helpers must be absent.");
Check(!instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "ZNet" &&
    m.Name is "Disconnect" or "DisconnectPeer" or "RPC_Disconnect" or "StopAll"), "The plugin must not call connection-disconnect or network-stop methods to enforce its presence.");
Check(!instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "Game" && m.Name == "Logout"), "The plugin must not log the player out for missing optional support.");
CustomAttribute[] dependencies = plugin.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInDependency").ToArray();
Check(dependencies.Length == 8 && dependencies.All(a => a.ConstructorArguments.Count == 2 && Convert.ToInt32(a.ConstructorArguments[1].Value) == 2), "All eight integration dependencies must remain soft.");
Check(dependencies.Any(a => Equals(a.ConstructorArguments[0].Value, "treextr.pathofvalheiman")), "PoV lookup integration must have a soft load-order dependency.");
Check(!artifact.MainModule.AssemblyReferences.Any(r => r.Name is "PathOfValheiman" or "VNEI"), "PoV/VNEI lookup integration must not add mandatory optional-mod assembly references.");
MethodDefinition active = plugin.Methods.Single(m => m.Name == "get_CosmeticRuntimeActive");
Check(new[] { "_instance", "_stopping" }.All(name => active.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == name)), "Automatic eligibility must retain the plugin lifetime boundary.");
Check(methods.Any(m => m.Name.StartsWith("<Awake>", StringComparison.Ordinal) && Calls(m, coordinator.FullName, "Install")), "Coordinator hooks must install during the real plugin Awake.");
Check(Calls(coordinator.Methods.Single(m => m.Name == "CanOptimize"), plugin.FullName, "get_CosmeticRuntimeActive"), "New optimization must stop when plugin teardown starts.");
Check(Equals(coordinator.Fields.Single(f => f.Name == "Protocol").Constant, 2), "Optional capability negotiation must use protocol 2.");
foreach (string kind in new[] { "Hello", "Report", "Grant" })
    Check(Equals(coordinator.Fields.Single(f => f.Name == kind + "Rpc").Constant, "sighsorry.InteropFixes.Cosmetics." + kind + ".v2"), "Protocol receiver namespace must isolate old mandatory coordinators: " + kind);
Check(!instructions.Any(i => i.OpCode == OpCodes.Ldstr && i.Operand is string value && value.StartsWith("sighsorry.InteropFixes.Cosmetics.") && value.EndsWith(".v1")), "No old mandatory protocol receiver may be registered.");
MethodDefinition install = coordinator.Methods.Single(m => m.Name == "Install");
Check(install.Body.Instructions.Count(i => i.Operand is MethodReference m && m.DeclaringType.FullName == coordinator.FullName && m.Name == "Patch") == 7, "Only the seven reviewed game lifecycle targets may be installed.");
foreach (string target in new[] { "Awake", "OnNewConnection", "RPC_PeerInfo", "Disconnect", "StopAll", "OnDestroy" })
    Check(install.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, target)), "Reviewed lifecycle target missing: " + target);
Check(coordinator.Methods.Single(m => m.Name == "BeforeDisconnect").ReturnType.FullName == "System.Void", "Disconnect hook must observe cleanup, not veto vanilla disconnection.");
Check(Calls(coordinator.Methods.Single(m => m.Name == "Report"), "ZNetPeer", "IsReady") &&
    Calls(coordinator.Methods.Single(m => m.Name == "ReceiveGrant"), "ZNetPeer", "IsReady"), "Report and positive grant eligibility must respect vanilla peer admission.");
Check(coordinator.Methods.Single(m => m.Name == "Publish").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "ReportRevision"), "Silent peers must not receive repeated unsupported grants.");
foreach (string path in args.Skip(1)) CoordinatorOriginalContracts.Verify(path, Check);
Console.WriteLine("Artifact SHA-256: " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))));
Console.WriteLine($"PASS: {checks} optional-install artifact and original coordinator contracts. Metadata/IL inspection only; no live game or network execution.");
