using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AwgPrototype;
using VpnMonitor.Client;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
internal static class ProfileHistoryChecks
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        var root=Path.Combine(Environment.CurrentDirectory,"artifacts","regression","profile-context",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var file=Path.Combine(root,"catalog.json");var id=Guid.NewGuid().ToString("N");const string config="private-key-fixture-do-not-store-as-plaintext";
        var catalog=new AwgProfileCatalog{SelectedId=id,Profiles=[new(id,"Офис")]};File.WriteAllText(file,JsonSerializer.Serialize(catalog));
        var context=ProfileContext.Create(config,file,selected=>{Require(selected==id,"Wrong profile path used");return config;});Require(context.ProfileId==id&&context.ProfileName=="Офис"&&context.Fingerprint.Length==64&&!JsonSerializer.Serialize(context).Contains(config),"Context identity/hash invalid or plaintext leaked");
        Require(ProfileContext.Create(config,file,_=>"different profile").ProfileId=="","Active copy mismatch guessed a profile");
        catalog.Profiles.Add(new(id,"Duplicate"));File.WriteAllText(file,JsonSerializer.Serialize(catalog));Require(ProfileContext.Create(config,file,_=>config).ProfileId=="","Duplicate catalog guessed a profile");File.WriteAllText(file,"invalid");Require(ProfileContext.Create(config,file,_=>config).ProfileId=="","Corrupt catalog guessed a profile");
        var other=Guid.NewGuid().ToString("N");var now=DateTimeOffset.Now;var old=new AwgConnectionEvent(now.AddMinutes(-2),"Connected","192.0.2.1:51820","Observed","",1);var a=old with{ProfileId=id,ProfileName="Офис",Reason="ResumeBackup"};var b=old with{ProfileId=other,ProfileName="Другой",Reason="ManualPassed"};
        File.WriteAllText(file,JsonSerializer.Serialize(new[]{old,a,b}));Require(AwgConnectionHistory.Read(file,id).Single()==a,"Profile reader mixed events");Require(AwgConnectionHistory.Read(file,"../escape").Length==0,"Unsafe history path accepted");Require(a.Description.Contains("выбран")&&!a.Description.Contains("установлена"),"Startup attempt reported confirmed connection");
        if(Application.Current is null){var app=new App(false);app.InitializeComponent();}Application.Current!.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        foreach(var theme in new[]{ThemeService.Dark,ThemeService.Light}) {
            ThemeService.Apply(theme);var rows=new[]{old,b};var scopedReads=0;
            var window=new AwgConnectionHistoryWindow(()=>rows,id,()=>new(){SelectedId=id,Profiles=[new(id,"Офис"),new(other,"Другой")]},requested=>{scopedReads++;return requested==id?[a]:[b];}){ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,Width=980,Height=480};window.Show();
            try {
                window.Refresh();var grid=(DataGrid)window.FindName("HistoryGrid");Require(grid.Items.Cast<AwgConnectionEvent>().Single()==a&&scopedReads>0,"Profile history must retain events absent from global last 200");var scopes=(ComboBox)window.FindName("ProfileScopeBox");
                scopes.SelectedItem=scopes.Items.Cast<AwgConnectionHistoryWindow.HistoryScope>().Single(s=>s.Id=="");Require(grid.Items.Cast<AwgConnectionEvent>().Single()==old,"Legacy events incorrectly assigned");rows=[b];window.Refresh();Require(((AwgConnectionHistoryWindow.HistoryScope)scopes.SelectedItem).Id==""&&grid.Items.Count==0,"Empty legacy selection must not jump to another profile");
                scopes.SelectedItem=scopes.Items.Cast<AwgConnectionHistoryWindow.HistoryScope>().Single(s=>s.Id==id);Require(grid.Items.Cast<AwgConnectionEvent>().Single()==a,"Profile selection lost scoped data");
                window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/regression/ui-0.1.80");using(var output=File.Create("artifacts/regression/ui-0.1.80/profile-history-"+theme+".png"))encoder.Save(output);
            }finally{window.Close();}
        }
        Console.WriteLine("PASS: service context revision binding/no plaintext, scoped history retention, legacy isolation and stable themed profile selection.");
    }
}
