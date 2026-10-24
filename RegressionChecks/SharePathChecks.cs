using VpnMonitor.Client.Services;

internal static class SharePathChecks
{
    public static void Run()
    {
        Require(NetworkSharePath.Create("10.20.0.2") == @"\\10.20.0.2", "Root shares path is invalid.");
        Require(NetworkSharePath.Create("10.20.0.2", "C$") == @"\\10.20.0.2\C$", "C$ path is invalid.");
        Require(NetworkSharePath.Create("10.20.0.2", "ADMIN$") == @"\\10.20.0.2\ADMIN$", "ADMIN$ path is invalid.");
        Require(NetworkSharePath.Create("10.20.0.2", " D$ ") == @"\\10.20.0.2\D$", "Custom drive share is invalid.");
        Require(NetworkSharePath.Create("10.20.0.2", "Отдел закупок") == @"\\10.20.0.2\Отдел закупок", "Unicode shares must be supported.");
        Require(NetworkSharePath.Create("2001:db8::1", "Backup") == @"\\2001-db8--1.ipv6-literal.net\Backup", "IPv6 UNC literal is invalid.");
        Require(NetworkSharePath.Create("fe80::1%12", "ADMIN$") == @"\\fe80--1s12.ipv6-literal.net\ADMIN$", "Scoped IPv6 UNC literal is invalid.");

        foreach (var name in new[] { "", " ", ".", "..", @"\\other\C$", @"C$\Windows", "../ADMIN$", "C:", "a\"b",
                     "a|b", "a,b", "a;b", "a*b", "a?b", "a\nb", new string('a', 81) })
        {
            try { NetworkSharePath.Create("10.20.0.2", name); throw new Exception("Invalid share accepted: " + name); }
            catch (ArgumentException) { }
        }
        foreach (var host in new[] { "", "not-an-ip", @"10.20.0.2\C$", "10.20.0.2 & other" })
        {
            try { NetworkSharePath.Create(host, "C$"); throw new Exception("Invalid host accepted."); }
            catch (ArgumentException) { }
        }
        foreach (var share in new string?[] { null, "C$", "ADMIN$", "Отдел закупок" })
        {
            var start = NetworkSharePath.ShellStartInfo("10.20.0.2", share);
            Require(start.FileName == NetworkSharePath.Create("10.20.0.2", share),
                "The actual Shell target must be the UNC folder, never Explorer's default folder.");
            Require(start.UseShellExecute && start.Verb == "open" && start.ArgumentList.Count == 0 && string.IsNullOrEmpty(start.Arguments),
                "Shell must open the folder object without an Explorer command line.");
        }
        Require(NetworkSharePath.ShellStartInfo("2001:db8::1", "Отдел закупок").FileName == @"\\2001-db8--1.ipv6-literal.net\Отдел закупок",
            "IPv6 and Unicode must remain part of the actual Shell target.");
        Console.WriteLine("PASS: UNC folder Shell targets, open verb, no Explorer arguments, validation, Unicode, IPv6.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}