using System.Runtime.InteropServices;
using VpnMonitor.Client.Services;
using AwgPrototype;

internal static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ValidateDelegate(IntPtr text, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ServiceDelegate(IntPtr text, IntPtr name, IntPtr dataDirectory);
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr AddDllDirectory(string directory);
    private const string ServiceName = "VpnMonitor.AwgPrototype";

    private static bool _serviceMode;
    private static void Stage(string value)
    {
        if (!_serviceMode) return;
        try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VpnMonitor", "AwgPrototype", "host-startup.log"), $"{DateTimeOffset.Now:o} {value}{Environment.NewLine}"); } catch { }
    }
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("--validate" or "--validate-protected" or "--check-routes" or "--check-protected-routes" or "--run-service"))
        {
            Console.WriteLine("Usage: AwgPrototype.Host --validate|--run-service <profile.conf>");
            return 2;
        }
        _serviceMode = args[0] == "--run-service";
        Stage("process-start");
        IntPtr configPointer = IntPtr.Zero, namePointer = IntPtr.Zero, library = IntPtr.Zero;
        try
        {
            var path = Path.GetFullPath(args[1]);
            if (new FileInfo(path).Length > 256 * 1024) throw new ArgumentException();
            var serviceMode = args[0] == "--run-service";
            if (serviceMode && Environment.UserInteractive) { Stage("interactive-refused"); return 5; }
            Stage("identity-checked");
            var protectedMode = serviceMode || args[0] is "--validate-protected" or "--check-protected-routes";
            var config = protectedMode ? MachineProfile.Read(path) : File.ReadAllText(path);
            Stage("profile-read");
            if (args[0] is "--check-routes" or "--check-protected-routes" || serviceMode)
            {
                _ = AwgConfigPreview.Parse(config);
                if (RouteGuard.HasConflict(config))
                {
                    Console.Error.WriteLine("Conflicting route detected. No tunnel started.");
                    return 7;
                }
                Stage("profile-read");
            if (args[0] is "--check-routes" or "--check-protected-routes") { Console.WriteLine("PASS: no conflicting route detected."); return 0; }
            }
            _ = AwgConfigPreview.Parse(config);
            if (config.Split('\n').Any(line =>
            {
                var key = line.Split('=', 2)[0].Trim();
                return new[] { "PreUp", "PostUp", "PreDown", "PostDown" }.Contains(key, StringComparer.OrdinalIgnoreCase);
            }))
            {
                Console.Error.WriteLine("Profiles containing script directives are refused.");
                return 3;
            }
            Stage("routes-and-scripts-checked");
            var dll = Path.Combine(AppContext.BaseDirectory, "tunnel.dll");
            if (!SetDefaultDllDirectories(0x00000800 | 0x00000400)) throw new InvalidOperationException();
            if (AddDllDirectory(AppContext.BaseDirectory) == IntPtr.Zero) throw new InvalidOperationException();
            library = NativeLibrary.Load(dll);
            Stage("library-loaded");
            configPointer = Marshal.StringToHGlobalUni(config);
            namePointer = Marshal.StringToHGlobalUni(ServiceName);
            var validate = Marshal.GetDelegateForFunctionPointer<ValidateDelegate>(
                NativeLibrary.GetExport(library, "VpnMonitorValidateConfig"));
            if (validate(configPointer, namePointer) == 0)
            {
                Console.Error.WriteLine("AWG engine rejected profile. Values are not logged.");
                return 4;
            }
            if (args[0] is "--validate" or "--validate-protected")
            {
                Console.WriteLine("PASS: AWG engine accepted configuration. No tunnel started.");
                return 0;
            }
            // Only the SCM-launched service is allowed to call the blocking engine entry.
            if (Environment.UserInteractive)
            {
                Console.Error.WriteLine("Run-service is permitted only when launched by Windows SCM.");
                return 5;
            }
            Stage("engine-validated");
            var run = Marshal.GetDelegateForFunctionPointer<ServiceDelegate>(
                NativeLibrary.GetExport(library, "VpnMonitorTunnelService"));
            var dataDirectory = Marshal.StringToHGlobalUni(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VpnMonitor", "AwgPrototype"));
            try { Stage("enter-service"); var result = run(configPointer, namePointer, dataDirectory); Stage($"service-return-{result}"); return result != 0 ? 0 : 6; }
            finally { Marshal.FreeHGlobal(dataDirectory); }
        }
        catch
        {
            Console.Error.WriteLine("Prototype failed: profile, engine or environment unavailable. Sensitive details suppressed.");
            return 1;
        }
        finally
        {
            if (configPointer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(configPointer);
            if (namePointer != IntPtr.Zero) Marshal.FreeHGlobal(namePointer);
            // Go runtime libraries stay loaded until this short-lived process exits.
        }
    }
}