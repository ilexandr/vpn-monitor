using VpnMonitor.Client.Services;

internal static class HistoryChecks
{
    public static void Run()
    {
        var history = new PeerHistory(3);
        history.Observe("own", "AWG", "Online");
        history.Observe("own", "AWG", "Online");
        history.Observe("other", "AWG", "Online");
        history.Observe("own", "AWG", "Offline");
        var result = history.Entries.Where(e => PeerHistoryExport.Matches(e, "own", "awg OFFline")).ToArray();
        Require(result.Length == 1 && result[0].Previous == "Online", "AND search must select only the requested peer and transition.");
        Require(!PeerHistoryExport.Matches(result[0], "other", ""), "Search must never include another peer.");
        Require(PeerHistoryExport.Matches(result[0], "own", "  \t "), "Whitespace search must show all peer events.");
        var csv = PeerHistoryExport.Create(result);
        Require(csv.Contains("Offline") && csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2,
            "Export must contain only the filtered snapshot.");
        var hostile = new PeerHistoryEntry(DateTimeOffset.Now, "own", "=formula", "semi;quote\"", "line\nnext");
        var escaped = PeerHistoryExport.Create([hostile]);
        Require(escaped.Contains("\"'=formula\"") && escaped.Contains("\"semi;quote\"\"\"") && escaped.Contains("\"line\nnext\""),
            "CSV must escape formulas, quotes, delimiters and newlines.");
        history.Observe("own", "Endpoint", "127.0.0.1");
        Require(history.Entries.Count == 3, "History must remain bounded.");
        history.RetainPeers(["own"]);
        Require(history.Entries.All(e => e.PeerId == "own"), "Removed peers must disappear from history.");
        history.Reset();
        Require(history.Entries.Count == 0, "Account reset must clear history.");
        Console.WriteLine("PASS: peer history AND search, peer isolation, filtered CSV, escaping, capacity and reset.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
