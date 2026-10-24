using System.ComponentModel;
using System.Runtime.InteropServices;
namespace VpnMonitor.Client.Services;
public sealed record AwgServiceSnapshot(uint State, uint ExitCode)
{
    public string Text => State switch { 0 => "Служба не установлена", 1 => "Остановлена", 2 => "Запускается", 3 => "Останавливается", 4 => "Работает", _ => "Переходное состояние" };
}
public static class AwgServiceStatus
{
    public const string ServiceName = "VpnMonitor.AwgPrototype";
    [StructLayout(LayoutKind.Sequential)]
    private struct Status { public uint Type, State, Controls, Win32Exit, SpecificExit, Checkpoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int info, out Status status, int size, out int needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    public static AwgServiceSnapshot Read()
    {
        var manager = OpenSCManager(null, null, 1);
        if(manager == IntPtr.Zero) throw new Win32Exception();
        var service = IntPtr.Zero;
        try
        {
            service = OpenService(manager, ServiceName, 4);
            if(service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if(error == 1060) return new(0, 0);
                throw new Win32Exception(error);
            }
            if(!QueryServiceStatusEx(service,0,out var status,Marshal.SizeOf<Status>(),out _)) throw new Win32Exception();
            return new(status.State,status.Win32Exit == 1066 ? status.SpecificExit : status.Win32Exit);
        }
        finally { if(service != IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
    }
}