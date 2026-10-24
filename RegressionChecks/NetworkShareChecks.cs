using System.ComponentModel;
using VpnMonitor.Client.Services;

internal static class NetworkShareChecks
{
    public static async Task RunAsync()
    {
        var probe = new FakeProbe();
        var connector = new FakeConnector();
        var service = new NetworkShareService(probe, connector);
        var owner = new IntPtr(12345);
        Require(await service.ConnectAsync("10.20.0.25", "C$", owner), "New SMB session must authenticate.");
        Require(connector.Calls == 1 && connector.Remote == @"\\10.20.0.25\IPC$" && connector.Prompt
            && connector.Owner == owner && connector.Apartment == ApartmentState.STA,
            "Windows credential UI must run on STA with its owner and IPC target.");
        connector.Connected = true;
        Require(await service.ConnectAsync("10.20.0.25", "ADMIN$", owner) && connector.Calls == 1,
            "An existing session must not prompt again.");

        connector.Connected = false;
        connector.Code = 1223;
        Require(!await service.ConnectAsync("10.20.0.25", null, owner), "Cancelling Windows login must not become an error.");
        foreach (var code in new[] { 1326, 5, 1219 })
        {
            connector.Code = code;
            try { await service.ConnectAsync("10.20.0.25", "C$", owner); throw new Exception("Connection error ignored."); }
            catch (Win32Exception ex) { Require(ex.NativeErrorCode == code, "Native error code must be preserved."); }
        }
        probe.Available = false;
        var calls = connector.Calls;
        try { await service.ConnectAsync("10.20.0.25", "C$", owner); throw new Exception("Unavailable SMB ignored."); }
        catch (System.IO.IOException) { }
        Require(connector.Calls == calls, "Unavailable SMB must not open credential prompts.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await service.ConnectAsync("10.20.0.25", "C$", owner, token: cancellation.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        Require(connector.Calls == calls, "Cancelled operation must not prompt.");
        Console.WriteLine("PASS: SMB login owner/STA, session reuse, cancellation, authentication/access/conflict errors, port preflight.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class FakeProbe : ISmbPortProbe
    {
        public bool Available = true;
        public Task<bool> IsAvailableAsync(string address, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Available);
        }
    }
    private sealed class FakeConnector : INetworkShareConnector
    {
        public bool Connected, Prompt;
        public int Code, Calls;
        public string? Remote;
        public IntPtr Owner;
        public ApartmentState Apartment;
        public bool IsConnected(string remote) => Connected;
        public int Connect(IntPtr owner, string remote, bool prompt)
        {
            Calls++;
            Owner = owner; Remote = remote; Prompt = prompt;
            Apartment = Thread.CurrentThread.GetApartmentState();
            return Code;
        }
    }
}