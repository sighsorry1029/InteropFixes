using System;
using System.Collections.Generic;
using InteropFixes;

internal static class Program
{
    private static int _checks;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    private static CosmeticAgreement Ready(long session = 100)
    {
        var agreement = new CosmeticAgreement();
        agreement.Reset(session);
        agreement.SetLocal(CosmeticAgreement.AllMask, true);
        return agreement;
    }

    private static void Main()
    {
        MembershipAndReadiness();
        InvalidAndReorderedReports();
        ReconnectAndReset();
        ReadinessAndCapabilities();
        OptionalMixedMembership();
        InvariantsAcrossInterleavings();
        Console.WriteLine($"{_checks} cosmetic agreement checks passed. Pure server readiness/capability state; no transport/game timing was simulated.");
    }

    private static void MembershipAndReadiness()
    {
        var agreement = new CosmeticAgreement();
        Check(agreement.Mask == 0 && agreement.Revision == 0, "Uninitialized agreement grants nothing");
        agreement.SetLocal(127, true);
        Check(agreement.Mask == 0, "No server session means no permission even with local capability");
        agreement.Reset(100);
        Check(agreement.Mask == 0 && agreement.Revision == 1 && !agreement.LocalReady, "New session requires explicit readiness");
        agreement.SetLocal(127, true);
        Check(agreement.Mask == 127, "Ready host with no remote members uses its local capability");
        long previous = agreement.Revision;
        agreement.AddPeer(1);
        Check(agreement.Mask == 0 && agreement.Revision > previous, "New unknown connection immediately invalidates cached permission");
        Check(agreement.Report(1, 100, 1, 63, false) && agreement.Mask == 0, "Capability report before peer readiness grants nothing");
        Check(agreement.Report(1, 100, 2, 63, true) && agreement.Mask == 63, "Known ready peer intersects local support");
        previous = agreement.Revision;
        agreement.AddPeer(1);
        Check(agreement.Mask == 63 && agreement.Revision == previous, "Repeated connection notification does not forget capability");
        agreement.AddPeer(2);
        Check(agreement.Mask == 0, "A second unknown peer invalidates prior agreement");
        Check(agreement.Report(2, 100, 1, 85, true) && agreement.Mask == (63 & 85), "Multiple ready capabilities intersect by feature bit");
        agreement.RemovePeer(2);
        Check(agreement.Mask == 63, "Leaving peer releases only its own capability restriction");
        previous = agreement.Revision;
        agreement.RemovePeer(2);
        agreement.AddPeer(0);
        agreement.AddPeer(-3);
        Check(agreement.Mask == 63 && agreement.Revision == previous, "Unknown removals and invalid connection tokens are inert");
        Check(agreement.Report(1, 100, 3, 0, true) && agreement.Mask == 0, "Valid peer with no optional Magic support disables optimization without rejecting membership");
        Check(agreement.Report(1, 100, 4, 63, true) && agreement.Mask == 63, "Later structural readiness can restore supported features");
    }

    private static void InvalidAndReorderedReports()
    {
        var agreement = Ready();
        agreement.AddPeer(1);
        Check(agreement.Report(1, 100, 5, 15, true), "Initial positive report revision is accepted");
        long previous = agreement.Revision;
        foreach (var report in new (long Token, long Session, long Revision, int Mask)[]
        {
            (2, 100, 6, 127), (1, 99, 6, 127), (1, 0, 6, 127), (1, -1, 6, 127),
            (1, 100, 0, 127), (1, 100, -1, 127), (1, 100, 5, 127), (1, 100, 4, 127),
            (1, 100, 6, 128), (1, 100, 6, -1), (1, 100, 6, int.MaxValue)
        })
            Check(!agreement.Report(report.Token, report.Session, report.Revision, report.Mask, true) &&
                  agreement.Mask == 15 && agreement.Revision == previous,
                "Malformed, unknown, cross-session and stale reports cannot overwrite permission");
        Check(agreement.Report(1, 100, 6, 7, true) && agreement.Mask == 7, "Rejected malformed report does not poison a valid retry revision");
        Check(!agreement.Report(1, 100, 5, 127, true) && agreement.Mask == 7, "Delayed optimistic report cannot undo a reduction");
        Check(agreement.Report(1, 100, 7, 127, false) && agreement.Mask == 0, "Readiness revocation removes all peer permission");
        Check(!agreement.Report(1, 100, 6, 127, true) && agreement.Mask == 0, "Old ready report cannot resurrect readiness");
    }

    private static void ReconnectAndReset()
    {
        var agreement = Ready();
        agreement.AddPeer(10);
        agreement.Report(10, 100, 90, 127, true);
        agreement.RemovePeer(10);
        agreement.AddPeer(11); // Same player's new connection always gets a fresh token.
        Check(agreement.Mask == 0, "Reconnect starts unknown rather than inheriting the previous connection");
        Check(!agreement.Report(10, 100, 91, 127, true) && agreement.Mask == 0, "Old connection reports cannot authorize reconnect");
        Check(agreement.Report(11, 100, 1, 3, true) && agreement.Mask == 3, "New connection can begin its own report sequence");
        long previous = agreement.Revision;
        agreement.Reset(100);
        Check(agreement.Revision > previous && agreement.Mask == 0, "Same-session reset never rewinds the server revision");
        Check(!agreement.Report(11, 100, 2, 127, true), "Reset clears old membership");
        agreement.Reset(101);
        agreement.SetLocal(127, true);
        agreement.AddPeer(12);
        Check(!agreement.Report(12, 100, 1000, 127, true) && agreement.Mask == 0, "Old session traffic cannot authorize the new world/session");
        Check(agreement.Report(12, 101, 1, 7, true) && agreement.Mask == 7, "Current session report is accepted independently");
        previous = agreement.Revision;
        bool rejected = false;
        try { agreement.Reset(0); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected && agreement.Session == 101 && agreement.Mask == 7 && agreement.Revision == previous, "Invalid reset does not corrupt current state");
    }

    private static void ReadinessAndCapabilities()
    {
        var agreement = Ready();
        agreement.AddPeer(1);
        agreement.Report(1, 100, 1, 127, true);
        agreement.SetLocal(127, false);
        Check(agreement.Mask == 0, "Local readiness loss immediately suppresses new authorization");
        agreement.Report(1, 100, 2, 127, true);
        Check(agreement.Mask == 0, "Peer reports cannot override an unready host");
        agreement.SetLocal(127, true);
        Check(agreement.Mask == 127, "Restored local readiness reuses current ready reports");
        agreement.SetLocal(15, true);
        Check(agreement.Mask == 15, "Local feature loss removes only affected permission bits");
        agreement.SetLocal(127, false);
        Check(agreement.Mask == 0, "Scene/probe readiness loss revokes permission");
        agreement.SetLocal(128, true);
        Check(agreement.Mask == 0 && agreement.LocalMask == 0 && !agreement.LocalReady, "Invalid local capability fails closed");
        agreement.SetLocal(0, true);
        Check(agreement.Mask == 0 && agreement.LocalReady, "Ready host without optional Magic support is a valid zero-support state");
        long previous = agreement.Revision;
        agreement.SetLocal(0, true);
        Check(agreement.Revision == previous, "Unchanged local state does not churn the grant revision");
    }

    private static void OptionalMixedMembership()
    {
        var agreement = Ready();
        agreement.AddPeer(1);
        agreement.Report(1, 100, 1, 63, true);
        agreement.AddPeer(2); // Native connection without an installed coordinator.
        agreement.AddPeer(3); // Installed coordinator without a supported MagicPlugin module.
        agreement.Report(3, 100, 1, 0, true);
        Check(agreement.Mask == 0, "Missing and unsupported optional peers veto otherwise supported membership");
        agreement.Report(1, 100, 2, 127, true);
        Check(agreement.Mask == 0, "A capable peer cannot compensate for missing or unsupported peers");
        agreement.RemovePeer(2);
        Check(agreement.Mask == 0, "Removing only the missing mod peer leaves the valid zero-capability veto");
        agreement.RemovePeer(3);
        Check(agreement.Mask == 127, "Removing all unsupported peers restores the supported survivors");
        agreement.AddPeer(4);
        agreement.Report(4, 100, 1, 0, true);
        agreement.Report(4, 100, 2, 15, true);
        Check(agreement.Mask == 15, "A late successful capability probe can replace a valid unsupported report");
        agreement.SetLocal(0, true);
        Check(agreement.Mask == 0 && agreement.LocalReady, "An unsupported server still vetoes capable clients without invalidating readiness");
        agreement.RemovePeer(4);
        Check(agreement.Mask == 0, "Remote departures cannot override absent local capability");
    }

    private static void InvariantsAcrossInterleavings()
    {
        // Independent model of membership/readiness exercises join/leave and capability report
        // permutations. This is state validation, not a claim of distributed atomicity.
        foreach (int[] order in new[] { new[] { 1, 2, 3 }, new[] { 3, 1, 2 }, new[] { 2, 3, 1 } })
        {
            var agreement = Ready();
            var known = new Dictionary<int, int?>();
            foreach (int token in order) { agreement.AddPeer(token); known[token] = null; }
            long previous = agreement.Revision;
            foreach (int token in order)
            {
                int mask = token == 1 ? 31 : token == 2 ? 85 : 127;
                agreement.Report(token, 100, 1, mask, true);
                known[token] = mask;
                int expected = 127;
                foreach (int? capability in known.Values) expected &= capability ?? 0;
                Check(agreement.Mask == expected && agreement.Revision > previous, "Report order preserves intersection and monotonic revisions");
                previous = agreement.Revision;
            }
            foreach (int token in order)
            {
                agreement.RemovePeer(token); known.Remove(token);
                int expected = 127;
                foreach (int? capability in known.Values) expected &= capability ?? 0;
                Check(agreement.Mask == expected && agreement.Revision > previous, "Leave order preserves remaining-peer intersection");
                previous = agreement.Revision;
            }
        }
    }
}
