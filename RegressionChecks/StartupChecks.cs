using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AwgPrototype;
using VpnMonitor.Client;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
internal static class StartupChecks
{
    private static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task RunAsync()
    {
        var root=Path.Combine(Environment.CurrentDirectory,"artifacts","regression","startup",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string Fixture(string name){var path=Path.Combine(root,name);Directory.CreateDirectory(path);return path;}
        var path=Fixture("migration");uint mode=3;var validations=0;var changes=0;
        AwgStartupSnapshot Read()=>new(mode,mode==2);
        void Configure(bool automatic){changes++;mode=automatic?2u:3u;}
        void Validate()=>validations++;
        StartupPolicyStore.Initialize(path,true,Read,Validate,Configure);Require(mode==2&&validations==1&&changes==1,"Legacy configured demand service must migrate once");
        StartupPolicyStore.Set(path,false,Validate,Configure);Require(mode==3&&validations==1,"Disabling must not need a profile or restart service");
        StartupPolicyStore.Initialize(path,true,Read,Validate,Configure);Require(mode==3&&changes==2,"Upgrade overwrote explicit manual choice");
        StartupPolicyStore.Set(path,true,Validate,Configure);Require(mode==2&&validations==2,"Explicit auto requires profile validation");
        var reinstall=Fixture("reinstall");StartupPolicyStore.Set(reinstall,true,Validate,Configure);mode=3;
        StartupPolicyStore.Initialize(reinstall,true,Read,Validate,Configure,true);Require(mode==2,"Reinstall must restore retained automatic policy");
        StartupPolicyStore.Set(reinstall,false,Validate,Configure);StartupPolicyStore.Initialize(reinstall,true,Read,Validate,Configure,true);Require(mode==3,"Reinstall must retain explicit manual mode");
        changes=3;
        mode=4;StartupPolicyStore.Initialize(Fixture("disabled"),true,Read,Validate,Configure);Require(mode==4&&changes==3,"Disabled service must not be enabled implicitly");
        mode=3;StartupPolicyStore.Initialize(Fixture("empty"),false,Read,Validate,Configure);Require(mode==3&&changes==3,"Fresh installation without profile must not auto-start");
        path=Fixture("invalid");try{StartupPolicyStore.Initialize(path,true,Read,()=>throw new ArgumentException(),Configure);throw new Exception("Invalid profile enabled");}catch(ArgumentException){}Require(changes==3&&!File.Exists(Path.Combine(path,"startup-configured")),"Invalid profile must not migrate or mark success");
        try{StartupPolicyStore.Set(path,true,Validate,_=>throw new IOException());throw new Exception("Configuration failure ignored");}catch(IOException){}Require(!File.Exists(Path.Combine(path,"startup-configured")),"SCM failure must remain retryable");
        Require(new AwgStartupSnapshot(2,true).Text.Contains("Windows")&&!new AwgStartupSnapshot(3).Automatic,"Startup display must reflect SCM mode");
        if(Application.Current is null){var app=new App(false);app.InitializeComponent();}Application.Current!.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        foreach(var theme in new[]{ThemeService.Dark,ThemeService.Light}) {
            ThemeService.Apply(theme);var id=Guid.NewGuid().ToString("N");var catalog=new AwgProfileCatalog{SelectedId=id,Profiles=[new(id,"Основной профиль")]};
            var window=new AwgManagementWindow((_,_)=>Task.FromResult(0),()=> (catalog,4u,"Работает"),readStartup:()=>new(mode,mode==2)){ShowInTaskbar=false,ShowActivated=false,Left=-10000,Top=-10000,Width=720,Height=560};
            window.Show();mode=3;window.RefreshState();var button=(Button)window.FindName("StartupButton");Require(button.IsEnabled&&button.Content.ToString()!.StartsWith("Включить"),"Manual startup UI");
            mode=2;window.RefreshState();Require(button.Content.ToString()!.StartsWith("Отключить"),"SCM changes from another session must refresh UI");
            var pending=new TaskCompletionSource<int>();var method=typeof(AwgManagementWindow).GetMethod("ExecuteOperationAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var operation=(Task)method.Invoke(window,["startup","manual",(Func<Task<int>>)(()=>pending.Task)])!;Require(!button.IsEnabled,"Startup must be locked during operation");pending.SetResult(0);await operation;
            window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/regression/ui-0.1.77");using(var output=File.Create("artifacts/regression/ui-0.1.77/startup-"+theme+".png"))encoder.Save(output);
            catalog.SelectedId=null;mode=3;window.RefreshState();Require(!button.IsEnabled,"Cannot enable startup without a selected profile");window.Close();
        }
        Console.WriteLine("PASS: AWG startup one-time migration, explicit manual choice across upgrades, disabled/empty/invalid profiles, SCM failures and themed multi-session UI.");
    }
}
