using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

internal static class Program
{
    private const string Prefix = "sighsorry.InteropFixes.Cosmetics.";
    private const string Hello = Prefix + "Hello.v2", Report = Prefix + "Report.v2", Grant = Prefix + "Grant.v2";
    private static int _checks;

    private sealed class Node : IDisposable
    {
        private readonly AssemblyLoadContext _context = new("cosmetic-fixture-" + Guid.NewGuid(), true);
        private readonly Type _endpoint;
        internal Node(bool server, long uid, int mask)
        {
            _endpoint = _context.LoadFromAssemblyPath(Assembly.GetExecutingAssembly().Location).GetType("Endpoint")!;
            Call("Init", server, uid, mask);
        }
        internal object? Call(string name, params object[] arguments)
        {
            try { return _endpoint.GetMethod(name)!.Invoke(null, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException!; }
        }
        internal T Get<T>(string name, params object[] args) => (T)Call(name, args)!;
        internal (string Name, byte[] Data)[] Drain(long key) => Get<(string, byte[])[]>("Drain", key);
        public void Dispose() { Call("Stop"); _context.Unload(); }
    }

    private static void Check(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
        _checks++;
    }

    private static void Main()
    {
        OrderingAndMembership();
        OptionalInstallation();
        NativeAdmissionAndProtocol();
        DelayedSceneAndCapabilities();
        ClientValidationAndDowngrades();
        CleanupAndSessionReset();
        FailureIsolation();
        Console.WriteLine($"{_checks} cosmetic coordinator checks passed using isolated endpoints and serialized RPC packets. Unity/Harmony/transport boundaries were simulated.");
    }

    private static void Transfer(Node source, long sourceKey, Node destination, long destinationKey)
    {
        foreach (var packet in source.Drain(sourceKey)) destination.Call("Deliver", destinationKey, packet.Name, packet.Data);
    }

    private static (long Session, long Nonce) ParseHeader(byte[] bytes)
    {
        var package = new ZPackage(bytes); package.ReadInt(); return (package.ReadLong(), package.ReadLong());
    }

    private static byte[] ReportPacket(long session, long nonce, long revision, int mask, bool ready)
    {
        var package = Header(session, nonce); package.Write(revision); package.Write(mask); package.Write(ready); return package.Bytes();
    }

    private static byte[] GrantPacket(long session, long nonce, long revision, int mask, params long[] participants)
    {
        var package = Header(session, nonce); package.Write(revision); package.Write(mask); package.Write(participants.Length);
        foreach (long participant in participants) package.Write(participant);
        return package.Bytes();
    }

    private static ZPackage Header(long session, long nonce, int protocol = 2)
    {
        var package = new ZPackage(); package.Write(protocol); package.Write(session); package.Write(nonce); return package;
    }

    private static void OrderingAndMembership()
    {
        using var server = new Node(true, 1, 127);
        using var client = new Node(false, 2, 63);
        Check(server.Get<bool>("Can", 127), "Ready server alone can use local capabilities");
        server.Call("Add", 10L, false, 0L);
        client.Call("Add", 20L, true, 0L);
        Check(!server.Get<bool>("Can", 1) && !client.Get<bool>("Can", 1), "Unknown connection immediately disables server and unnegotiated client");
        var initial = server.Drain(10);
        Check(initial.Any(p => p.Name == Hello) && !initial.Any(p => p.Name == Grant), "Server emits hello while grants wait for native peer admission");
        var header = ParseHeader(initial.Single(p => p.Name == Hello).Data);
        client.Call("Deliver", 20L, Grant, GrantPacket(header.Session, header.Nonce, 1, 127, 1, 2));
        Check(!client.Get<bool>("Can", 1), "A reordered grant before hello cannot authorize the client");
        foreach (var packet in initial) client.Call("Deliver", 20L, packet.Name, packet.Data);
        Check(client.Get<long>("Session") == header.Session && !client.Get<bool>("Can", 1), "Grant preceding hello is harmless; hello pins session and connection nonce");
        Transfer(client, 20, server, 10);
        client.Call("Ready", 20L, 1L);
        Transfer(client, 20, server, 10);
        Check(!server.Get<bool>("Can", 1), "Client-ready report does not authorize server's unadmitted peer");
        server.Call("Deliver", 10L, Report, ReportPacket(header.Session, header.Nonce, 1, 0, false));
        Check(server.Drain(10).Length == 0, "Reordered pre-admission reports do not publish grants before native admission");
        server.Call("Ready", 10L, 2L);
        Transfer(server, 10, client, 20);
        Check(server.Get<bool>("Can", 63) && client.Get<bool>("Can", 63) && !server.Get<bool>("Can", 64),
            "Admission applies the newest pending capability, ignoring older reports, and grants the common feature set");
        Check(server.Get<bool>("Participant", 2L) && client.Get<bool>("Participant", 1L), "Ready participants are shared for replay sender checks");

        server.Call("Add", 11L, false, 0L);
        Check(!server.Get<bool>("Can", 1), "Late unknown peer revokes current server permission immediately");
        Check(client.Get<bool>("Can", 1), "Documented asynchronous window: prior client grant persists until downgrade arrives");
        var revoke = server.Drain(10);
        foreach (var packet in revoke) client.Call("Deliver", 20L, packet.Name, packet.Data);
        Check(!client.Get<bool>("Can", 1), "Delivered zero grant revokes existing client permission");
        server.Call("Disconnect", 11L);
        var restore = server.Drain(10);
        foreach (var packet in restore) client.Call("Deliver", 20L, packet.Name, packet.Data);
        Check(server.Get<bool>("Can", 63) && client.Get<bool>("Can", 63), "Unknown peer leaving restores surviving ready membership");
        foreach (var packet in revoke) client.Call("Deliver", 20L, packet.Name, packet.Data);
        Check(client.Get<bool>("Can", 63), "Delayed older zero grant cannot replace a later revision");

        server.Call("Deliver", 10L, Report, ReportPacket(header.Session, header.Nonce, 100, 0, true));
        Transfer(server, 10, client, 20);
        Check(!server.Get<bool>("Can", 1) && !client.Get<bool>("Can", 1), "Valid zero-support optional module report disables optimization without disconnecting");
        server.Call("Deliver", 10L, Report, ReportPacket(header.Session, header.Nonce, 99, 127, true));
        Check(!server.Get<bool>("Can", 1), "Stale optimistic capability report cannot undo downgrade");
        server.Call("Deliver", 10L, Report, ReportPacket(header.Session, header.Nonce + 1, 101, 127, true));
        Check(!server.Get<bool>("Can", 1), "Wrong connection nonce cannot grant server permission");
        server.Call("Deliver", 10L, Report, ReportPacket(header.Session + 1, header.Nonce, 101, 127, true));
        Check(!server.Get<bool>("Can", 1), "Wrong server session cannot grant server permission");
    }

    private static void OptionalInstallation()
    {
        using (var client = new Node(false, 2, 127))
        {
            client.Call("Add", 20L, true, 0L);
            client.Call("Ready", 20L, 1L);
            client.Call("Local", 63);
            client.Call("Refresh");
            client.Call("Advance", 600f);
            client.Call("Refresh");
            Check(client.Get<long>("Session") == 0 && !client.Get<bool>("Can", 1),
                "A server without the coordinator never provides a session or cosmetic permission");
            Check(client.Drain(20).Length == 0, "Missing server does not trigger capability probes or periodic RPC traffic");
            Check(client.Get<bool>("Connected", 20L) && client.Get<int>("Rejections") == 0,
                "Missing optional server integration does not reject an admitted native connection");
        }

        using var server = new Node(true, 1, 127);
        using var supported = new Node(false, 2, 63);
        server.Call("Add", 10L, false, 2L);
        supported.Call("Add", 20L, true, 1L);
        Transfer(server, 10, supported, 20);
        Transfer(supported, 20, server, 10);
        Transfer(server, 10, supported, 20);
        Check(server.Get<bool>("Can", 63) && supported.Get<bool>("Can", 63), "Supported endpoints negotiate without any configuration handshake");

        // UID 3 is a native admitted connection with no coordinator at all.
        server.Call("Add", 11L, false, 3L);
        var absentClientPackets = server.Drain(11);
        Check(absentClientPackets.Length == 1 && absentClientPackets[0].Name == Hello,
            "An unreported optional peer receives only the one connection hello, never a grant");
        var unmoddedRpc = new ZRpc();
        Check(unmoddedRpc.Deliver(absentClientPackets[0].Name, absentClientPackets[0].Data) == 0,
            "The simulated unmodded endpoint ignores the unknown cosmetic hello");
        Check(!server.Get<bool>("Can", 1) && supported.Get<bool>("Can", 1),
            "Missing client vetoes server immediately; a prior remote grant remains until its asynchronous revoke arrives");
        Transfer(server, 10, supported, 20);
        Check(!supported.Get<bool>("Can", 1), "A delivered revoke restores fallback on the installed client in mixed membership");
        server.Call("Refresh");
        server.Call("Advance", 600f);
        server.Call("Refresh");
        Check(server.Drain(11).Length == 0 && !server.Get<bool>("Can", 1),
            "Silence never grants permission and repeated readiness events do not send grants to an unreported peer");
        Check(server.Get<bool>("Connected", 11L) && server.Get<int>("Rejections") == 0,
            "The missing client stays connected instead of being timed out or rejected by this coordinator");

        using var noMagic = new Node(false, 4, 0);
        server.Call("Add", 12L, false, 4L);
        noMagic.Call("Add", 40L, true, 1L);
        Transfer(server, 12, noMagic, 40);
        var noMagicReports = noMagic.Drain(40);
        Check(noMagicReports.Length == 1 && IsReadyReport(noMagicReports[0].Data, 0),
            "An installed client without verified MagicPlugin capabilities reports ready with mask zero");
        foreach (var packet in noMagicReports) server.Call("Deliver", 12L, packet.Name, packet.Data);
        Transfer(server, 12, noMagic, 40);
        server.Call("Disconnect", 11L);
        Transfer(server, 10, supported, 20);
        Transfer(server, 12, noMagic, 40);
        Check(!server.Get<bool>("Can", 1) && !supported.Get<bool>("Can", 1) && !noMagic.Get<bool>("Can", 1),
            "A valid unsupported member keeps fallback active even after the missing mod member leaves");
        Check(server.Get<bool>("Connected", 12L) && noMagic.Get<bool>("Connected", 40L) && server.Get<int>("Rejections") == 0,
            "Zero-capability peers are accepted normally without a forced disconnect");
        server.Call("Disconnect", 12L);
        Transfer(server, 10, supported, 20);
        Check(server.Get<bool>("Can", 63) && supported.Get<bool>("Can", 63),
            "Removing the last unsupported member restores the intersection of the supported survivors");

        using var noMagicServer = new Node(true, 5, 0);
        using var capableClient = new Node(false, 6, 127);
        noMagicServer.Call("Add", 50L, false, 6L);
        capableClient.Call("Add", 60L, true, 5L);
        Transfer(noMagicServer, 50, capableClient, 60);
        Transfer(capableClient, 60, noMagicServer, 50);
        Transfer(noMagicServer, 50, capableClient, 60);
        Check(!noMagicServer.Get<bool>("Can", 1) && !capableClient.Get<bool>("Can", 1) &&
              noMagicServer.Get<bool>("Connected", 50L) && capableClient.Get<bool>("Connected", 60L),
            "A coordinator server without MagicPlugin capabilities preserves the session but cannot grant suppression");
    }

    private static void NativeAdmissionAndProtocol()
    {
        using var client = new Node(false, 2, 127);
        client.Call("Add", 20L, true, 0L);
        foreach (int protocol in new[] { 1, 3 })
        {
            client.Call("Deliver", 20L, Hello, Header(100, 200, protocol).Bytes());
            Check(client.Get<long>("Session") == 0 && client.Drain(20).Length == 0,
                "Incompatible protocol hello cannot pin a session or elicit a report");
        }
        Check(client.Get<int>("Deliver", 20L, Prefix + "Hello.v1", Header(100, 200, 1).Bytes()) == 0,
            "Legacy RPC names have no registered coordinator handler");
        client.Call("Deliver", 20L, Hello, Header(100, 200).Bytes());
        var unready = client.Drain(20);
        Check(unready.Length == 1 && IsDisabledReport(unready[0].Data),
            "A current hello before native admission reports unavailable readiness");
        client.Call("Deliver", 20L, Grant, GrantPacket(100, 200, 1, 127, 1, 2));
        Check(!client.Get<bool>("Can", 1), "A positive grant after hello still cannot bypass native peer admission");
        client.Call("Ready", 20L, 1L);
        var ready = client.Drain(20);
        Check(ready.Length == 1 && IsReadyReport(ready[0].Data, 127) && !client.Get<bool>("Can", 1),
            "Native admission publishes readiness but cannot reuse a pre-admission grant");
        client.Call("Deliver", 20L, Grant, WithProtocol(GrantPacket(100, 200, 2, 127, 1, 2), 1));
        Check(!client.Get<bool>("Can", 1), "An incompatible grant header cannot authorize an otherwise ready client");
        client.Call("Deliver", 20L, Grant, GrantPacket(100, 200, 2, 127, 1, 2));
        Check(client.Get<bool>("Can", 127), "A fresh current-protocol grant completes native admission without ServerSync");
        client.Call("Ready", 20L, 1L);
        Check(client.Drain(20).Length == 0 && client.Get<int>("Rejections") == 0,
            "Repeated native admission does not churn unchanged reports or reject the client");

        using var server = new Node(true, 1, 127);
        server.Call("Add", 10L, false, 2L);
        var header = ParseHeader(server.Drain(10).Single(p => p.Name == Hello).Data);
        server.Call("Deliver", 10L, Report, WithProtocol(ReportPacket(header.Session, header.Nonce, 100, 127, true), 1));
        Check(!server.Get<bool>("Can", 1) && server.Drain(10).Length == 0,
            "An incompatible report never counts as demonstrated protocol support or receives a grant");
        server.Call("Deliver", 10L, Report, ReportPacket(header.Session, header.Nonce, 100, 128, true));
        Check(!server.Get<bool>("Can", 1) && server.Drain(10).Length == 0,
            "An invalid capability mask never marks an optional peer as supporting grants");
        server.Call("Deliver", 10L, Report, ReportPacket(header.Session, header.Nonce, 1, 127, true));
        Check(server.Get<bool>("Can", 127) && server.Drain(10).Any(p => p.Name == Grant),
            "A rejected protocol or malformed report cannot poison a subsequent valid report's revision");
        server.Call("Deliver", 10L, Report, ReportPacket(header.Session, header.Nonce, 1, 0, true));
        Check(server.Get<bool>("Can", 127) && server.Drain(10).Length == 0,
            "A repeated accepted report revision cannot mutate capabilities or trigger another grant");
        Check(server.Get<bool>("Connected", 10L) && server.Get<int>("Rejections") == 0,
            "Protocol mismatch disables only the optimization and preserves the native connection");
    }

    private static void DelayedSceneAndCapabilities()
    {
        using var server = new Node(true, 1, 127);
        using var client = new Node(false, 2, 0);
        client.Call("SceneStart");
        server.Call("Add", 10L, false, 2L);
        client.Call("Add", 20L, true, 1L);
        Transfer(server, 10, client, 20);
        Transfer(client, 20, server, 10);
        Transfer(server, 10, client, 20);
        client.Call("Local", 63);
        Check(client.Drain(20).Length == 0 && !server.Get<bool>("Can", 1) && !client.Get<bool>("Can", 1),
            "Capabilities discovered before scene readiness cannot prematurely enable the integration");
        client.Call("SceneReady");
        var ready = client.Drain(20);
        Check(ready.Length == 1 && IsReadyReport(ready[0].Data, 63),
            "Scene completion after native admission sends the deferred ready capability report");
        foreach (var packet in ready) server.Call("Deliver", 10L, packet.Name, packet.Data);
        Transfer(server, 10, client, 20);
        Check(server.Get<bool>("Can", 63) && client.Get<bool>("Can", 63), "Delayed scene completion grants only the common capability intersection");
        client.Call("SceneDestroy");
        Check(!client.Get<bool>("Can", 1), "Scene teardown revokes local permission before the remote round trip");
        Transfer(client, 20, server, 10);
        Transfer(server, 10, client, 20);
        Check(!server.Get<bool>("Can", 1), "Scene teardown report revokes server permission");
        client.Call("SceneStart");
        client.Call("SceneReady");
        Transfer(client, 20, server, 10);
        Transfer(server, 10, client, 20);
        Check(!client.Get<bool>("Can", 1), "A new ready scene cannot inherit capabilities from the destroyed scene");
        client.Call("Local", 31);
        Transfer(client, 20, server, 10);
        Transfer(server, 10, client, 20);
        Check(server.Get<bool>("Can", 31) && client.Get<bool>("Can", 31) && !client.Get<bool>("Can", 32),
            "Capabilities discovered after scene completion negotiate a new bounded intersection");
        client.Call("Stop");
        client.Call("SceneReady");
        Check(!client.Get<bool>("Can", 1) && client.Drain(20).Length == 0,
            "A late native scene callback after module stop cannot restore permission");
    }

    private static byte[] WithProtocol(byte[] packet, int protocol)
    {
        var result = (byte[])packet.Clone();
        BitConverter.GetBytes(protocol).CopyTo(result, 0);
        return result;
    }

    private static void ClientValidationAndDowngrades()
    {
        using var client = new Node(false, 2, 127);
        client.Call("Add", 20L, true, 1L);
        client.Call("Deliver", 20L, Hello, Header(100, 200).Bytes());
        byte[] grant = GrantPacket(100, 200, 10, 127, 1, 2, 3);
        int consumed = client.Get<int>("Deliver", 20L, Grant, grant);
        Check(consumed == grant.Length && client.Get<bool>("Can", 127), "Valid grant consumes exactly its packet and enables supported features");
        Check(client.Get<bool>("Participant", 3L) && !client.Get<bool>("Participant", 99L), "Replay accepts only authenticated participant identities");
        foreach (byte[] bad in new[]
        {
            GrantPacket(101, 200, 11, 0, 1, 2), GrantPacket(100, 201, 11, 0, 1, 2),
            GrantPacket(100, 200, 9, 0, 1, 2), GrantPacket(100, 200, 10, 0, 1, 2),
            GrantPacket(100, 200, 11, 128, 1, 2), GrantPacket(100, 200, 11, 0, 1, 1),
            GrantPacket(100, 200, 11, 0, 0, 2), GrantPacket(100, 200, 11, 0)
        })
        {
            client.Call("Deliver", 20L, Grant, bad);
            Check(client.Get<bool>("Can", 127), "Invalid identity/revision/mask/participant grant cannot mutate active permission");
        }
        var trailing = grant.Concat(new byte[] { 1 }).ToArray();
        client.Call("Deliver", 20L, Grant, trailing);
        Check(client.Get<bool>("Can", 127), "Trailing bytes cannot replace an existing grant");
        client.Call("Deliver", 20L, Hello, Header(999, 999).Bytes());
        Check(client.Get<long>("Session") == 100, "Established hello identity cannot be rebound on the same connection");
        client.Call("Add", 21L, false, 9L);
        client.Call("Deliver", 21L, Hello, Header(100, 300).Bytes());
        client.Call("Deliver", 21L, Grant, GrantPacket(100, 300, 1000, 127, 1, 2));
        Check(!client.Get<bool>("Can", 1), "Non-server connection cannot authorize client optimization");
        // A fresh legitimate server connection starts its own handshake after the extra connection reset.
        client.Call("Disconnect", 21L);
        client.Call("Disconnect", 20L);
        client.Call("Add", 22L, true, 1L);
        client.Call("Deliver", 22L, Grant, GrantPacket(100, 200, 999, 127, 1, 2, 3));
        Check(client.Get<long>("Session") == 0 && !client.Get<bool>("Can", 1),
            "A reconnect cannot inherit a previous connection grant before its new hello");
        client.Call("Deliver", 22L, Hello, Header(101, 201).Bytes());
        client.Call("Deliver", 22L, Grant, GrantPacket(100, 200, 1000, 127, 1, 2, 3));
        Check(client.Get<long>("Session") == 101 && !client.Get<bool>("Can", 1),
            "An old high-revision grant cannot authorize the new connection after hello");
        client.Call("Deliver", 22L, Grant, GrantPacket(101, 201, 1, 127, 1, 2, 3));
        client.Call("Local", 3);
        Check(client.Get<bool>("Can", 3) && !client.Get<bool>("Can", 4), "Local capability reduction applies before any server round trip");
        client.Call("RuntimeActive", false);
        Check(!client.Get<bool>("Can", 1), "Stopped runtime rejects a stale positive grant immediately");
        client.Call("RuntimeActive", true);
        Check(!client.Get<bool>("Can", 1), "Restored runtime readiness cannot bypass a fresh server grant");
        client.Call("Deliver", 22L, Grant, GrantPacket(101, 201, 2, 3, 1, 2));
        Check(client.Get<bool>("Participant", 3L), "Recently departed sender is retained briefly for already committed events");
        client.Call("Advance", 31f);
        Check(!client.Get<bool>("Participant", 3L), "Departed replay identity expires after bounded grace");
    }

    private static void CleanupAndSessionReset()
    {
        using var server = new Node(true, 1, 127);
        server.Call("Add", 10L, false, 2L);
        var hello = server.Drain(10).Single(p => p.Name == Hello); var header = ParseHeader(hello.Data);
        server.Call("Deliver", 10L, Report, ReportPacket(header.Session, header.Nonce, 1, 127, true));
        Check(server.Get<bool>("Can", 127), "Server accepts current connection's complete report");
        server.Call("Replace", 10L, Hello);
        server.Call("Disconnect", 10L);
        Check(server.Get<bool>("Has", 10L, Hello) && !server.Get<bool>("Has", 10L, Report) && !server.Get<bool>("Has", 10L, Grant),
            "Cleanup removes only exact owned RPC handlers and preserves a foreign replacement");
        server.Call("Deliver", 10L, Hello, Array.Empty<byte>());
        Check(server.Get<int>("ForeignCalls") == 1, "Preserved replacement remains callable after module cleanup");
        server.Call("Add", 11L, false, 2L);
        Check(!server.Get<bool>("Can", 1), "Reconnecting same player UID does not inherit previous connection capability");
        server.Call("Deliver", 11L, Report, ReportPacket(header.Session, header.Nonce, 2, 127, true));
        Check(!server.Get<bool>("Can", 1), "Previous connection's signed header is not valid for a new connection");
        server.Call("StopNetwork");
        Check(server.Get<long>("Session") == 0 && !server.Get<bool>("Can", 1) && server.Get<int>("Count", 11L) == 0,
            "Network stop clears permissions, session and owned handlers");
        server.Call("RestartNetwork", true, 1L);
        Check(server.Get<long>("Session") != 0 && server.Get<long>("Session") != header.Session, "World/network restart allocates a fresh server session");
    }

    private static void FailureIsolation()
    {
        using (var client = new Node(false, 2, 127))
        {
            client.Call("Add", 20L, true, 1L);
            client.Call("Deliver", 20L, Hello, Header(100, 200).Bytes());
            client.Call("Deliver", 20L, Grant, GrantPacket(100, 200, 1, 127, 1, 2));
            client.Drain(20);
            client.Call("Deliver", 20L, Grant, Header(100, 200).Bytes()); // truncated after authenticated header
            Check(!client.Get<bool>("Can", 1), "Malformed authenticated message disables local transmission");
            var failureReport = client.Drain(20).LastOrDefault(p => p.Name == Report);
            Check(failureReport.Data != null && IsDisabledReport(failureReport.Data), "Faulted client reports unavailable readiness back to the server");
            client.Call("Deliver", 20L, Grant, GrantPacket(100, 200, 2, 127, 1, 2));
            client.Call("Refresh");
            Check(!client.Get<bool>("Can", 1), "Fault latch prevents later grant/readiness events from silently restoring transmission");
            Check(client.Get<string[]>("Logs").Length > 0, "Negotiation failure emits a diagnostic");
        }
        using (var server = new Node(true, 1, 127))
        {
            server.Call("Add", 10L, false, 2L);
            server.Call("FailCleanup", 10L, true);
            server.Call("Disconnect", 10L);
            Check(!server.Get<bool>("Can", 1), "RPC cleanup failure cannot escape native disconnect and keeps optimization disabled");
            server.Call("Refresh");
            Check(!server.Get<bool>("Can", 1), "Cleanup error remains disabled after later readiness notification");
            server.Call("FailCleanup", 10L, false);
        }
        using (var server = new Node(true, 1, 127))
        {
            server.Call("Add", 10L, false, 2L);
            var first = ParseHeader(server.Drain(10).Single(p => p.Name == Hello).Data);
            server.Call("Deliver", 10L, Report, ReportPacket(first.Session, first.Nonce, 1, 127, true));
            server.Call("Add", 11L, false, 3L);
            var second = ParseHeader(server.Drain(11).Single(p => p.Name == Hello).Data);
            server.Call("Deliver", 11L, Report, ReportPacket(second.Session, second.Nonce, 1, 127, true));
            server.Drain(10); server.Drain(11);
            server.Call("FailSend", 10L, true);
            server.Call("Local", 0);
            var grant = server.Drain(11).LastOrDefault(p => p.Name == Grant);
            Check(grant.Data != null && GrantMask(grant.Data) == 0, "One broken socket does not prevent healthy peers receiving the zero grant");
            Check(!server.Get<bool>("Can", 1), "Broadcast failure leaves server transmission disabled");
        }
    }

    private static bool IsDisabledReport(byte[] data)
    {
        var message = new ZPackage(data);
        message.ReadInt(); message.ReadLong(); message.ReadLong(); message.ReadLong();
        return message.ReadInt() == 0 && !message.ReadBool();
    }

    private static bool IsReadyReport(byte[] data, int expectedMask)
    {
        var message = new ZPackage(data);
        message.ReadInt(); message.ReadLong(); message.ReadLong(); message.ReadLong();
        return message.ReadInt() == expectedMask && message.ReadBool() && message.GetPos() == message.Size();
    }

    private static int GrantMask(byte[] data)
    {
        var message = new ZPackage(data);
        message.ReadInt(); message.ReadLong(); message.ReadLong(); message.ReadLong();
        return message.ReadInt();
    }
}
