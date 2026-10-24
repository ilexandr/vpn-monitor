using System.IO;
using System.Security.Cryptography;
using System.Text;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

internal static class HistoryArchiveChecks
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VpnMonitor-history-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.Now;
            var store = new PeerHistoryArchive(directory, () => now);
            var scope = PeerHistoryArchive.Scope("https://example.invalid/", "ОФИС");
            Require(scope == PeerHistoryArchive.Scope("https://EXAMPLE.invalid:443", "офис"), "Equivalent server/account spellings must share scope.");
            var otherAccount = PeerHistoryArchive.Scope("https://example.invalid", "other");
            var otherServer = PeerHistoryArchive.Scope("https://other.invalid", "ОФИС");
            Require(scope != otherAccount && scope != otherServer, "Server and account must isolate archives.");
            var current = new PeerHistoryEntry(now, "allowed", "AWG", "Online", "Offline");
            store.Save(scope, [current, current with { PeerId = "revoked" }, current with { Time = now.AddDays(-8) }], 1);
            var path = Path.Combine(directory, scope + ".dpapi");
            Require(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("Offline"), "Archive must not expose plaintext event values.");
            var reopened = new PeerHistoryArchive(directory, () => now);
            Require(reopened.Load(scope).Length == 2 && reopened.Load(otherAccount).Length == 0, "Archive must survive a new store instance and expire old events.");
            var history = new PeerHistory();
            history.RestoreAuthorized(reopened.Load(scope), ["allowed"]);
            Require(history.Entries.Count == 1 && history.Entries[0].PeerId == "allowed", "Only freshly authorized peers may be restored.");
            history.Observe("allowed", "AWG", "Online");
            Require(history.Entries[0].Previous == "—", "Restart must establish a baseline rather than imply monitoring during downtime.");
            history.Observe("second", "AWG", "Online");
            history.ClearPeer("allowed");
            Require(history.Entries.Count == 1 && history.Entries[0].PeerId == "second", "Clear must affect only the selected peer.");
            history.Observe("allowed", "AWG", "Online");
            Require(history.Entries[0].Previous == "—", "Cleared peer must get a fresh baseline.");
            store.Save(scope, [], 3);
            store.Save(scope, [current], 2);
            Require(store.Load(scope).Length == 0, "An older queued write must not resurrect cleared events.");
            store.ClearAll(5);
            store.Save(scope, [current], 4);
            Require(!File.Exists(path), "Disabled persistence must reject earlier queued saves.");
            store.Save(scope, [current], 6);
            File.Copy(path, Path.Combine(directory, otherAccount + ".dpapi"));
            try { store.Load(otherAccount); throw new Exception("Archive copied between accounts was accepted."); }
            catch (CryptographicException) { }
            File.WriteAllBytes(path, [1, 2, 3]);
            try { store.Load(scope); throw new Exception("Corrupt archive accepted."); }
            catch (CryptographicException) { }
            store.Save(scope, [current], 7);
            var crowded = Enumerable.Range(0, 2100).Select(i => current with { Time = now.AddSeconds(-i) });
            store.Save(scope, crowded, 8);
            Require(store.Load(scope).Length == 2000, "Stored history must remain bounded.");
            now = now.AddDays(8);
            Require(store.Load(scope).Length == 0, "Retention must also apply when loading an untouched archive.");
            var hugePath = Path.Combine(directory, otherServer + ".dpapi");
            using (var huge = File.Create(hugePath)) huge.SetLength(4_000_001);
            try { store.Load(otherServer); throw new Exception("Oversized archive accepted."); }
            catch (InvalidDataException) { }
            now = DateTimeOffset.Now;
            store.ClearAll(9);
            var staleTemp = Path.Combine(directory, scope + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(staleTemp, "fixture");
            File.SetLastWriteTimeUtc(staleTemp, now.UtcDateTime.AddDays(-2));
            for (var i = 0; i < 22; i++)
            {
                var accountScope = PeerHistoryArchive.Scope("https://example.invalid", "user" + i);
                store.Save(accountScope, [current with { Time = now }], 10 + i);
            }
            Require(Directory.GetFiles(directory, "*.dpapi").Length == 20 && !File.Exists(staleTemp),
                "Account files and abandoned temporary files must also be bounded.");
            var settings = new ClientSettings { SavePeerHistory = false };
            Require(!SettingsTransfer.Import(SettingsTransfer.Export(settings)).SavePeerHistory, "Settings transfer must preserve persistence opt-out.");
            Console.WriteLine("PASS: DPAPI archive restart, account/server isolation, authorized restore, retention/capacity, fresh baseline, clear, stale-write rejection, corruption and settings round-trip.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
