using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.ViewModels;
using VpnMonitor.Client.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--rolling-log-child")
        {
            for (var i = 0; i < 10; i++) RollingLog.Append(args[1], args[2] + "-" + i + "\n");
            return 0;
        }
        VpnMonitor.Client.App.ConfigureLogDirectory(System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "regression", "logs"));
        SettingsService.ConfigureDefaultDirectory(System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "regression", "settings"));
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var exitCode = 1;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (args.Contains("--live-admin-users")) { await AdminUsersChecks.RunLiveAsync(); exitCode=0; return; }
                if (args.Contains("--admin-users-only")) { await AdminUsersChecks.RunAsync(); exitCode=0; return; }
                if (args.Contains("--live-file-plan")) { await UpdateFilePlanChecks.RunLiveAsync(); exitCode=0; return; }
                if (args.Contains("--live-access")) { await LiveAccessChecks.RunAsync(); exitCode = 0; return; }
                if (args.Contains("--interface-only")) { await InterfaceChecks.RunAsync(); exitCode = 0; return; }
                if (args.Contains("--monitoring-only")) { await MonitoringChecks.RunAsync(); exitCode = 0; return; }
                if (args.Contains("--uninstall-admin-ipc")) { await UpdateChecks.RunAdminIpcAsync(true); exitCode = 0; return; }
                if (args.Contains("--update-admin-ipc")) { await UpdateChecks.RunAdminIpcAsync(); exitCode = 0; return; }
                await CountChecks.RunAsync();
                await UpdateChecks.RunAsync();
                if (args.Contains("--updates-only")) { exitCode = 0; return; }
                CheckTunnelHealth();
                HistoryChecks.Run();
                HistoryArchiveChecks.Run();
                await HistoryRefreshChecks.RunAsync();
                await ConnectionRecoveryChecks.RunAsync();
                await OwnedWindowChecks.RunAsync();
                await InterfaceChecks.RunAsync();
                await MonitoringChecks.RunAsync();
                await AdminUsersChecks.RunAsync();
                SettingsRecoveryChecks.Run();
                await DiagnosticChecks.RunAsync();
                await CheckTunnelProbeAsync();
                if (args.Contains("--health-only")) { exitCode = 0; return; }
                if (args.Contains("--access-only"))
                {
                    await AccessChecks.RunAsync();
                    exitCode = 0;
                    return;
                }
                if (args.Contains("--actions-only"))
                {
                    await ActionChecks.RunAsync();
                    SharePathChecks.Run();
                    await NetworkShareChecks.RunAsync();
                    exitCode = 0;
                    return;
                }
                if (args.Contains("--shares-only"))
                {
                    SharePathChecks.Run();
                    await NetworkShareChecks.RunAsync();
                    exitCode = 0;
                    return;
                }
                await AccessChecks.RunAsync();
                CheckNotifications();
                Console.WriteLine("PASS: notification snapshots, watched peers, filters, reset, duplicates.");
                await ReachabilityChecks.RunAsync();
                await PingChecks.RunAsync();
                await TraceRouteChecks.RunAsync();
                await ActionChecks.RunAsync();
                SharePathChecks.Run();
                await NetworkShareChecks.RunAsync();
                await CheckRefreshAsync();
                Console.WriteLine("PASS: timeout, retained data, repeated failure, recovery, connection refusal.");
                exitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
        }));
        Dispatcher.Run();
        return exitCode;
    }

    private static async Task CheckTunnelProbeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        try
        {
            if (!await TunnelHealthMonitor.ProbeAsync(uri, CancellationToken.None)) throw new Exception("Healthy probe failed");
            using var accepted = await listener.AcceptTcpClientAsync();
        }
        finally { listener.Stop(); }
        if (await TunnelHealthMonitor.ProbeAsync(uri, CancellationToken.None)) throw new Exception("Refused probe passed");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await TunnelHealthMonitor.ProbeAsync(uri, cancelled.Token); throw new Exception("Cancellation was swallowed"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: tunnel TCP probe success, refusal and cancellation.");
    }

    private static void CheckTunnelHealth()
    {
        var monitor = new TunnelHealthMonitor();
        void Check(bool condition) { if (!condition) throw new Exception("Tunnel health regression"); }
        Check(monitor.CheckedAt is null && monitor.LastReachableAt is null);
        Check(!monitor.Observe(false, false) && !monitor.IsDegraded);
        Check(!monitor.Observe(true, false));
        Check(!monitor.Observe(true, false));
        Check(monitor.Observe(true, false) && monitor.IsDegraded);
        Check(!monitor.Observe(true, false));
        Check(monitor.Observe(true, true) && !monitor.IsDegraded);
        var success = monitor.LastReachableAt;
        Check(success is not null && success == monitor.CheckedAt);
        monitor.Observe(true, false); monitor.Observe(false, null);
        Check(monitor.LastReachableAt == success);
        Check(!monitor.Observe(true, false) && !monitor.IsDegraded);
        monitor.Reset();
        Check(monitor.CheckedAt is null && monitor.LastReachableAt is null && !monitor.IsDegraded);
        monitor.Observe(true, null);
        Check(!monitor.Observe(true, false) && !monitor.IsDegraded);
        Console.WriteLine("PASS: tunnel health confirmation, notification deduplication, recovery, stop and unknown reset.");
    }

    private static void CheckNotifications()
    {
        var tracker = new PeerStatusTracker();
        var settings = new ClientSettings { StatusNotificationPeerIds = ["watched"] };
        static Peer P(string id, bool online) => new() { Id = id, Status = online ? "running" : "stopped" };
        Require(tracker.Update([P("watched", false)], settings).Count == 0, "First snapshot must be silent.");
        Require(tracker.Update([P("watched", true), P("other", false)], settings).Count == 1, "Watched Online transition missing.");
        Require(tracker.Update([P("watched", true), P("other", true)], settings).Count == 0, "Unchanged or unwatched peers must be silent.");
        Require(tracker.Update([P("watched", false)], settings).Single().Id == "watched", "Watched Offline transition missing.");
        settings.NotifyWhenOnline = false;
        Require(tracker.Update([P("watched", true)], settings).Count == 0, "Online filter ignored.");
        settings.NotifyWhenOffline = false;
        Require(tracker.Update([P("watched", false)], settings).Count == 0, "Offline filter ignored.");
        settings.NotifyWhenOnline = settings.NotifyWhenOffline = true;
        settings.StatusNotificationsEnabled = false;
        Require(tracker.Update([P("watched", true)], settings).Count == 0, "Global switch ignored.");
        settings.StatusNotificationsEnabled = true;
        Require(tracker.Update([P("watched", true)], settings).Count == 0, "Re-enabling must not replay suppressed changes.");
        tracker.Reset();
        Require(tracker.Update([P("watched", false)], settings).Count == 0, "Account reset must discard old snapshot.");
        Require(tracker.Update([P("watched", true), P("watched", true)], settings).Count == 1, "Duplicate IDs must not duplicate events.");
        Require(tracker.Update([], settings).Count == 0, "Removed peers must not produce Offline events.");
        Require(tracker.Update([P("watched", false)], settings).Count == 0, "Reappearing peers need a fresh baseline.");
    }

    private static async Task CheckRefreshAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var viewModel = new MainViewModel();
        viewModel.Configure($"http://127.0.0.1:{port}", "test", "test");
        var obsolete = viewModel.RefreshAsync();
        viewModel.Configure($"http://127.0.0.1:{port}", "new-session", "test");
        await obsolete;
        Require(!viewModel.HasError && !viewModel.AwgState.IsCurrent && viewModel.CurrentUsername == "new-session",
            "An obsolete request must not affect the new session.");
        var previousPeer = new Peer { Status = "running", AwgState = viewModel.AwgState };
        viewModel.Peers.Add(previousPeer);
        viewModel.AwgState.IsCurrent = true;

        // Listening without replying reproduces a tunnel that silently drops traffic.
        await viewModel.RefreshAsync();
        Require(viewModel.HasError && !viewModel.IsBusy, "Timeout must become a recoverable UI error.");
        Require(viewModel.Peers.Count == 1 && ReferenceEquals(viewModel.Peers[0], previousPeer), "Last successful data must remain.");
        Require(!previousPeer.AwgState.IsCurrent && previousPeer.IsOnline, "Lost API must mark status unknown while preserving the raw last status.");
        await viewModel.RefreshAsync();
        Require(viewModel.HasError && !viewModel.IsBusy, "A second refresh must run after timeout.");

        listener.Stop();
        using var recoveryListener = new TcpListener(IPAddress.Loopback, port);
        recoveryListener.Start();
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await recoveryListener.AcceptTcpClientAsync(stop.Token);
                    using var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var received = await stream.ReadAsync(buffer, stop.Token);
                    if (received == 0) continue;
                    const string json = "{\"items\":[],\"username\":\"test\",\"is_admin\":true}";
                    var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}");
                    try { await stream.WriteAsync(response, stop.Token); }
                    catch (System.IO.IOException) { /* Timed-out requests may already be closed. */ }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        await viewModel.RefreshAsync();
        Require(!viewModel.HasError && !viewModel.IsBusy && viewModel.Peers.Count == 0, $"Refresh must recover once the server replies: {viewModel.ErrorMessage}");
        Require(previousPeer.AwgState.IsCurrent, "Fresh API data must restore the shared indicator, including open cards.");
        viewModel.MarkAwgDataStale();
        Require(!previousPeer.AwgState.IsCurrent, "Reference server loss must invalidate AWG immediately.");
        await viewModel.RefreshAsync();
        Require(viewModel.AwgState.IsCurrent, "Only a successful API refresh restores AWG freshness.");
        stop.Cancel();
        await server;
        recoveryListener.Stop();
        await viewModel.RefreshAsync();
        Require(viewModel.HasError && !viewModel.IsBusy, "Connection refusal must remain recoverable.");
        Require(!viewModel.AwgState.IsCurrent, "Connection refusal must invalidate AWG freshness.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
