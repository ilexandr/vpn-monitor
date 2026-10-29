using System.ComponentModel;
using System.Runtime.InteropServices;
namespace VpnMonitor.Client.Services;
public sealed record AwgStartupSnapshot(uint Mode, bool Delayed = false)
{
    public bool Automatic => Mode == 2;
    public string Text => Mode switch { 2 => Delayed ? "Автозапуск: включён, с задержкой Windows" : "Автозапуск: включён", 3 => "Автозапуск: выключен (запуск вручную)", 4 => "Служба отключена в Windows", _ => "Автозапуск: состояние недоступно" };
}
// Fixed service name; configuration requires an elevated administrator. No tunnel restart here.
public static class AwgServiceStartup
{
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct Config { public uint Type, Start, Error; public IntPtr Binary, Group; public uint Tag; public IntPtr Dependencies, Account, Display; }
    [StructLayout(LayoutKind.Sequential)] private struct Delayed { [MarshalAs(UnmanagedType.Bool)] public bool Enabled; }
    [StructLayout(LayoutKind.Sequential)] private struct ActionInfo { public uint Type, Delay; }
    [StructLayout(LayoutKind.Sequential)] private struct FailureInfo { public uint Reset; public IntPtr Reboot, Command; public uint Count; public IntPtr Actions; }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenService(IntPtr manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig(IntPtr service,IntPtr config,uint size,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig2(IntPtr service,uint level,IntPtr config,uint size,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig(IntPtr service,uint type,uint start,uint error,string? binary,string? group,IntPtr tag,string? dependencies,string? account,string? password,string? display);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig2(IntPtr service,uint level,IntPtr config);
    [DllImport("advapi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    private static T WithService<T>(uint access,Func<IntPtr,T> action)
    {
        var manager=OpenSCManager(null,null,1);if(manager==IntPtr.Zero)throw new Win32Exception();
        var service=IntPtr.Zero;
        try {service=OpenService(manager,AwgServiceStatus.ServiceName,access);if(service==IntPtr.Zero)throw new Win32Exception();return action(service);}
        finally {if(service!=IntPtr.Zero)CloseServiceHandle(service);CloseServiceHandle(manager);}
    }
    public static AwgStartupSnapshot Read()=>WithService(1,service=> {
        QueryServiceConfig(service,IntPtr.Zero,0,out var needed);if(needed==0||needed>65536)throw new Win32Exception();
        var buffer=Marshal.AllocHGlobal((int)needed);
        try {
            if(!QueryServiceConfig(service,buffer,needed,out _))throw new Win32Exception();var mode=Marshal.PtrToStructure<Config>(buffer).Start;
            var delayed=false;if(mode==2){if(!QueryServiceConfig2(service,3,buffer,needed,out _))throw new Win32Exception();delayed=Marshal.PtrToStructure<Delayed>(buffer).Enabled;}
            return new AwgStartupSnapshot(mode,delayed);
        } finally {Marshal.FreeHGlobal(buffer);}
    });
    public static void Configure(bool automatic)=>WithService(3,service=> {
        // Delay is cleared before switching to demand start; Windows rejects delayed demand services.
        var buffer=Marshal.AllocHGlobal(4);
        try {
            if(!automatic){Marshal.WriteInt32(buffer,0);if(!ChangeServiceConfig2(service,3,buffer))throw new Win32Exception();}
            if(!ChangeServiceConfig(service,uint.MaxValue,automatic?2u:3u,uint.MaxValue,null,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception();
            if(automatic){Marshal.WriteInt32(buffer,1);if(!ChangeServiceConfig2(service,3,buffer))throw new Win32Exception();}
            return true;
        } finally {Marshal.FreeHGlobal(buffer);}
    });
    public static void ConfigureRecovery()=>WithService(18,service=> {
        // Two retries after failures, then stop; an orderly/manual stop is never restarted.
        var actions=Marshal.AllocHGlobal(Marshal.SizeOf<ActionInfo>()*3);var info=Marshal.AllocHGlobal(Marshal.SizeOf<FailureInfo>());
        try {
            var items=new[]{new ActionInfo{Type=1,Delay=30000},new ActionInfo{Type=1,Delay=60000},new ActionInfo{Type=0,Delay=0}};
            for(var i=0;i<items.Length;i++)Marshal.StructureToPtr(items[i],IntPtr.Add(actions,i*Marshal.SizeOf<ActionInfo>()),false);
            Marshal.StructureToPtr(new FailureInfo{Reset=86400,Count=3,Actions=actions},info,false);
            if(!ChangeServiceConfig2(service,2,info))throw new Win32Exception();
            Marshal.WriteInt32(info,1);if(!ChangeServiceConfig2(service,4,info))throw new Win32Exception();return true;
        } finally {Marshal.FreeHGlobal(info);Marshal.FreeHGlobal(actions);}
    });
}
