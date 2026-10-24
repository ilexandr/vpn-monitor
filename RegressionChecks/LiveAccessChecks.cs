using System.Text.Json;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.ViewModels;

// Explicit opt-in only. Credentials arrive through stdin and are never saved or printed.
internal static class LiveAccessChecks
{
    internal sealed record Input(string ServerUrl, string AdminUsername, string AdminPassword,
        string Username, string Password, string EmptyUsername, string EmptyPassword, string[] ExpectedPeerIds);

    public static async Task RunAsync()
    {
        var input = JsonSerializer.Deserialize<Input>(await Console.In.ReadLineAsync()
            ?? throw new InvalidOperationException("Live access input missing."))
            ?? throw new InvalidOperationException("Live access input invalid.");
        using var model = new MainViewModel();
        model.Configure(input.ServerUrl, input.AdminUsername, input.AdminPassword);
        await model.RefreshAsync();
        Check(!model.HasError && model.IsAdmin && model.Peers.Count > input.ExpectedPeerIds.Length,
            "Live administrator snapshot must include clients outside the test group.");

        model.Configure(input.ServerUrl, input.Username, input.Password);
        Check(model.Peers.Count == 0 && model.Groups.Count == 0 && !model.IsAdmin,
            "Account change must clear the previous scope immediately.");
        await model.RefreshAsync();
        Check(!model.HasError && model.IsRestrictedUser && model.RestrictedGroup == input.Username,
            "Live restricted identity missing.");
        Check(model.SelectedGroup == input.Username && model.Groups.All(g => g.Name == input.Username),
            "Live restricted group selection incorrect.");
        Check(input.ExpectedPeerIds.ToHashSet(StringComparer.Ordinal).SetEquals(model.Peers.Select(p => p.Id)),
            "Desktop and Web client scopes differ.");
        Check(input.ExpectedPeerIds.ToHashSet(StringComparer.Ordinal).SetEquals(model.PeersView.Cast<Peer>().Select(p => p.Id)),
            "Desktop filtered view differs from the allowed scope.");
        Check(model.Peers.All(p => p.Groups.All(g => string.Equals(g, input.Username, StringComparison.OrdinalIgnoreCase))),
            "Desktop exposes an unrelated group name.");

        model.Configure(input.ServerUrl, input.EmptyUsername, input.EmptyPassword);
        await model.RefreshAsync();
        Check(!model.HasError && model.IsRestrictedUser && model.Peers.Count == 0 && model.TotalCount == 0,
            "Account without a matching group must see an empty list.");

        model.Configure(input.ServerUrl, input.Username, input.Password + "-invalid");
        await model.RefreshAsync();
        Check(model.HasError && model.Peers.Count == 0 && model.Groups.Count == 0 && !model.AwgState.IsCurrent,
            "Invalid live credentials must not retain previous clients.");
        Console.WriteLine("PASS: real server Desktop/Web scope, administrator transition, empty group and invalid password.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
