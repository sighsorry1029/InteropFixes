using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InteropFixes;

// Each fixture endpoint runs in its own AssemblyLoadContext, so production static
// coordinator/model state and simulated ZNet singletons are genuinely independent.
public static class Endpoint
{
    private static readonly Dictionary<long, ZNetPeer> Peers = new();
    private static int _foreignCalls;
    public static void Init(bool server, long uid, int mask)
    {
        ZNet.instance = new ZNet { Server = server }; ZNet.UID = uid;
        ZNetScene.instance = new ZNetScene();
        CosmeticNetworkCoordinator.Install(new HarmonyLib.Harmony("CosmeticCoordinator.Tests"));
        CosmeticNetworkCoordinator.SetLocalCapabilities(mask);
    }
    public static void Add(long key, bool server, long uid)
    {
        var peer = new ZNetPeer { m_server = server, m_uid = uid }; Peers.Add(key, peer);
        Hook("BeforeNewConnection", ZNet.instance!, peer);
        ZNet.instance!.Peers.Add(peer);
        Hook("AfterNewConnection", peer);
    }
    public static void Ready(long key, long uid)
    {
        Peers[key].m_uid = uid;
        Hook("AfterPeerInfo", Peers[key].m_rpc);
    }
    public static (string Name, byte[] Data)[] Drain(long key)
    {
        var rpc = Peers[key].m_rpc; var output = rpc.Outbox.ToArray(); rpc.Outbox.Clear(); return output;
    }
    public static int Deliver(long key, string name, byte[] payload) => Peers[key].m_rpc.Deliver(name, payload);
    public static bool Can(int mask) => CosmeticNetworkCoordinator.CanOptimize(mask);
    public static long Session() => CosmeticNetworkCoordinator.SessionToken;
    public static bool Participant(long uid) => CosmeticNetworkCoordinator.IsParticipant(uid);
    public static void RuntimeActive(bool active)
    {
        InteropFixesPlugin.CosmeticRuntimeActive = active;
        CosmeticNetworkCoordinator.RefreshReadiness();
    }
    public static void SceneStart()
    {
        ZNetScene.instance = new ZNetScene();
        Hook("SceneStarting", ZNetScene.instance);
    }
    public static void SceneReady() => Hook("SceneReady", ZNetScene.instance!);
    public static void SceneDestroy() => Hook("SceneDestroying", ZNetScene.instance!);
    public static void Refresh() => CosmeticNetworkCoordinator.RefreshReadiness();
    public static void Local(int mask) => CosmeticNetworkCoordinator.SetLocalCapabilities(mask);
    public static void Disconnect(long key)
    {
        Hook("BeforeDisconnect", Peers[key]);
        ZNet.instance!.Peers.Remove(Peers[key]);
    }
    public static void Replace(long key, string name) => Peers[key].m_rpc.Register(name, (_, _) => _foreignCalls++);
    public static bool Has(long key, string name) => Peers[key].m_rpc.Has(name);
    public static int Count(long key) => Peers[key].m_rpc.HandlerCount;
    public static bool Connected(long key) => ZNet.instance!.Peers.Contains(Peers[key]);
    public static int Rejections() => ZNet.instance!.DisconnectCalls;
    public static int ForeignCalls() => _foreignCalls;
    public static void FailSend(long key, bool fail) => Peers[key].m_rpc.FailInvoke = fail;
    public static void FailCleanup(long key, bool fail) => Peers[key].m_rpc.FailUnregister = fail;
    public static void Advance(float seconds) => UnityEngine.Time.realtimeSinceStartup += seconds;
    public static void StopNetwork() => Hook("NetworkStopping");
    public static void Stop() => CosmeticNetworkCoordinator.Stop();
    public static void RestartNetwork(bool server, long uid)
    {
        ZNet.instance = new ZNet { Server = server }; ZNet.UID = uid;
        Hook("NetworkAwake", ZNet.instance);
    }
    public static string[] Logs() => InteropFixesPlugin.ModLogger.Messages.ToArray();
    private static void Hook(string name, params object[] arguments)
    {
        try { typeof(CosmeticNetworkCoordinator).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException!; }
    }
}
