using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
using AwgPrototype;

internal static class Program
{
    private static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VpnMonitor", "AwgPrototype");
    private static readonly string CatalogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VpnMonitor", "AwgStatus", "profiles.json");
    private static readonly string Engine = Path.Combine(AppContext.BaseDirectory, "tunnel.dll");
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte Validate(IntPtr text, IntPtr name);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr AddDllDirectory(string path);

    internal static int Execute(string action,string argument) => Main([action,argument]);
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length == 0 || args.Length == 2 && args[0] == "--theme" && args[1] is "light" or "dark")
        {
            if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                System.Windows.Forms.MessageBox.Show("Запустите панель через «Запуск от имени администратора». Обычный VPN Monitor не требует повышения прав.","VPN Monitor AWG");
                return 10;
            }
            var app = new VpnMonitor.Client.App(false);
            app.InitializeComponent();
            ThemeService.Apply(args.Length == 2 ? args[1] : new SettingsService().Load().Theme);
            var window = new VpnMonitor.Client.AwgManagementWindow((action, argument) => Task.Run(() => Execute(action, argument)));
            using var updater = new UpdateParticipant("Manager", () => window.Dispatcher.BeginInvoke(new Action(() => window.Close())));
            app.Run(window);
            return 0;
        }
        if(args.Length != 2 || args[0] is not ("import" or "select" or "remove" or "start" or "stop" or "adopt")) return 2;
        if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 10;
        try
        {
            // Protected file lock serializes privileged requests across all Windows sessions.
            using var gate = new FileStream(Path.Combine(Data, "manager.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var catalog = JsonSerializer.Deserialize<AwgProfileCatalog>(File.ReadAllText(CatalogFile)) ?? throw new InvalidOperationException();
            var state = AwgServiceStatus.Read().State;
            switch(args[0])
            {
                case "import":
                {
                    var path = Path.GetFullPath(args[1]);
                    if(new FileInfo(path).Length > 256 * 1024) return 11;
                    var config = File.ReadAllText(path);
                    ValidateConfig(config);
                    var name = Path.GetFileNameWithoutExtension(path);
                    if(string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) return 11;
                    var baseName=name;
                    for(var suffix=2;catalog.Profiles.Any(p=>string.Equals(p.Name,name,StringComparison.CurrentCultureIgnoreCase));suffix++)
                        name=baseName[..Math.Min(baseName.Length,65)]+" ("+suffix+")";
                    var id = Guid.NewGuid().ToString("N");
                    ProtectProfile(config, ProfilePath(id));
                    catalog.Profiles.Add(new(id, name));
                    if(state == 1) Select(catalog, id);
                    Save(catalog);
                    return 0;
                }
                case "select":
                    if(state != 1) return 12;
                    Select(catalog, args[1]); Save(catalog); return 0;
                case "remove":
                {
                    var profile = Find(catalog,args[1]);
                    if(catalog.SelectedId == profile.Id)
                    {
                        if(state!=1)return 12;
                        catalog.SelectedId=null;
                        File.Delete(Path.Combine(Data,"profile.dpapi"));
                    }
                    File.Delete(ProfilePath(profile.Id));
                    catalog.Profiles.Remove(profile); Save(catalog); return 0;
                }
                case "adopt":
                    if(catalog.Profiles.Count != 0 || !File.Exists(Path.Combine(Data,"profile.dpapi"))) return 12;
                    var adoptedId = Guid.NewGuid().ToString("N");
                    var existing = MachineProfile.Read(Path.Combine(Data,"profile.dpapi"));
                    ValidateConfig(existing);
                    File.Copy(Path.Combine(Data,"profile.dpapi"),ProfilePath(adoptedId));
                    catalog.Profiles.Add(new(adoptedId,"TEST"));
                    catalog.SelectedId=adoptedId; Save(catalog); return 0;
                case "start":
                    if(state == 4) return 0;
                    if(state != 1 || catalog.SelectedId is null) return 12;
                    var selected = Find(catalog,catalog.SelectedId);
                    var configToStart = MachineProfile.Read(ProfilePath(selected.Id));
                    ValidateConfig(configToStart);
                    if(RouteGuard.HasConflict(configToStart)) return 14;
                    Select(catalog,selected.Id);
                    return Control("start",4);
                case "stop":
                    if(state == 1) return 0;
                    if(state != 4) return 12;
                    return Control("stop",1);
            }
            return 2;
        }
        catch(IOException) { return 15; }
        catch { return 16; }
    }
    private static AwgProfileInfo Find(AwgProfileCatalog catalog,string id) =>
        catalog.Profiles.Single(p => p.Id == id && Guid.TryParseExact(p.Id,"N",out _));
    private static string ProfilePath(string id)
    {
        if(!Guid.TryParseExact(id,"N",out _)) throw new ArgumentException();
        return Path.Combine(Data,id+".dpapi");
    }
    private static void Select(AwgProfileCatalog catalog,string id)
    {
        var profile = Find(catalog,id);
        var config = MachineProfile.Read(ProfilePath(profile.Id));
        ValidateConfig(config);
        var target = Path.Combine(Data,"profile.dpapi");
        var next = target+".new";
        File.Copy(ProfilePath(profile.Id),next,true);
        File.Move(next,target,true);
        catalog.SelectedId=profile.Id;
    }
    private static void Save(AwgProfileCatalog catalog)
    {
        var next=CatalogFile+".new";
        File.WriteAllText(next,JsonSerializer.Serialize(catalog));
        File.Move(next,CatalogFile,true);
    }
    private static void ProtectProfile(string config,string path)
    {
        // Encrypt within this process; plaintext never enters command-line arguments or a staging file.
        ProfileProtector.Write(config,path);
    }
    private static int Control(string action,uint desired)
    {
        var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"sc.exe"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add(action); info.ArgumentList.Add(AwgServiceStatus.ServiceName);
        using var process=Process.Start(info)!;
        var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(40000)) { process.Kill(); return 17; }
        if(process.ExitCode != 0) return 17;
        var until=DateTime.UtcNow.AddSeconds(35);
        while(DateTime.UtcNow<until)
        {
            var state=AwgServiceStatus.Read().State;
            if(state==desired) { if(action=="stop") Thread.Sleep(2000); return 0; }
            if(action=="start" && state==1) return 17;
            Thread.Sleep(250);
        }
        return 17;
    }
    private static void ValidateConfig(string config)
    {
        _=AwgConfigPreview.Parse(config);
        if(config.Split('\n').Any(l => new[]{"PreUp","PostUp","PreDown","PostDown"}.Contains(l.Split('=',2)[0].Trim(),StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException();
        if(!SetDefaultDllDirectories(0x800|0x400) || AddDllDirectory(Path.GetDirectoryName(Engine)!)==IntPtr.Zero) throw new InvalidOperationException();
        var module=NativeLibrary.Load(Engine);
        var validate=Marshal.GetDelegateForFunctionPointer<Validate>(NativeLibrary.GetExport(module,"VpnMonitorValidateConfig"));
        var text=Marshal.StringToHGlobalUni(config); var name=Marshal.StringToHGlobalUni(AwgServiceStatus.ServiceName);
        try { if(validate(text,name)==0) throw new ArgumentException(); }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(text); Marshal.FreeHGlobal(name); }
    }
}
