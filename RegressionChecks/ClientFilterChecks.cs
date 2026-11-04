using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VpnMonitor.Client;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
using VpnMonitor.Client.ViewModels;
internal static class ClientFilterChecks
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task RunAsync()
    {
        if(Application.Current is null){var app=new App(false);app.InitializeComponent();}Application.Current!.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        foreach(var theme in new[]{ThemeService.Dark,ThemeService.Light}) {
            var directory=Path.Combine(Environment.CurrentDirectory,"artifacts","regression","client-filters-"+theme);var service=new SettingsService(directory);
            var window=new MainWindow(service,new PeerHistoryArchive(Path.Combine(directory,"history")));
            try {
                ThemeService.Apply(theme);var model=(MainViewModel)window.DataContext;
                var a=new Peer{Id="a",Name="PrestigBH_Inna",Groups=["Office"],Status="running"};var b=new Peer{Id="b",Name="PrestigBH_srv",Groups=["Office"],Status="offline"};var c=new Peer{Id="c",Name="Other",Groups=["Other"],Status="offline"};model.Peers.Add(a);model.Peers.Add(b);model.Peers.Add(c);
                model.SetFavoriteIds(["c"]);model.SetNotificationIds(["a","b","not-authorized"]);model.NotificationsOnly=true;Require(model.NotificationCount==2&&model.PeersView.Cast<Peer>().Count()==2,"Notification count/filter must only include authorized loaded peers");
                model.Filter="offline";model.SearchText="Prestig BH";Require(model.PeersView.Cast<Peer>().Single()==b,"Notification mode must combine with AND search and status filter");
                var content=(FrameworkElement)window.Content;content.Measure(new Size(900,610));content.Arrange(new Rect(0,0,900,610));content.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.DataBind);
                var clear=(Button)window.FindName("ClearSearchButton");Require(clear.Visibility==Visibility.Visible,"Clear button hidden for nonempty search");clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.DataBind);Require(model.SearchText==""&&clear.Visibility==Visibility.Collapsed&&model.NotificationsOnly&&model.Filter=="offline","Clear must only reset search");
                ((Button)window.FindName("FavoriteClientsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(model.FavoritesOnly&&!model.NotificationsOnly,"Favorite and notifications modes must be exclusive");
                ((Button)window.FindName("NotificationClientsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(!model.FavoritesOnly&&model.NotificationsOnly&&model.SelectedGroup is null,"Notification sidebar did not select its mode");
                var toggle=typeof(MainWindow).GetMethod("ToggleStatusNotification",BindingFlags.Instance|BindingFlags.NonPublic)!;
                // Initialize the persisted watch list, then remove a visible row via the actual command.
                var settings=(ClientSettings)typeof(MainWindow).GetField("_settings",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;settings.StatusNotificationPeerIds=["a","b"];model.SetNotificationIds(settings.StatusNotificationPeerIds);toggle.Invoke(window,[b]);Require(model.NotificationCount==1&&!model.PeersView.Cast<Peer>().Any(),"Turning off notifications must remove the row immediately");
                model.AwgState.IsCurrent=true;var scope=PeerHistoryArchive.Scope("http://example.test","admin");var view=SavedViewService.Capture("Watched",scope,model,settings);model.FavoritesOnly=true;Require(SavedViewService.Apply(view,scope,model,settings)&&model.NotificationsOnly&&!model.FavoritesOnly&&settings.NotificationsOnly,"Saved view lost notifications mode");
                var imported=SettingsTransfer.Import(SettingsTransfer.Export(settings));Require(imported.NotificationsOnly,"Settings transfer lost sidebar mode");
                ((Button)window.FindName("AllClientsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(!model.NotificationsOnly&&!model.FavoritesOnly,"All clients did not exit filtered mode");
                model.Filter="all";model.SearchText="Prestig";settings.StatusNotificationPeerIds=["a","b"];typeof(MainViewModel).GetProperty("TotalCount")!.SetValue(model,3);typeof(MainViewModel).GetProperty("OnlineCount")!.SetValue(model,1);model.SetNotificationIds(["a","b"]);((Button)window.FindName("NotificationClientsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));content.Measure(new Size(900,610));content.Arrange(new Rect(0,0,900,610));content.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.DataBind);
                var bitmap=new RenderTargetBitmap(900,610,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/regression/ui-0.1.79");using(var output=File.Create("artifacts/regression/ui-0.1.79/client-filters-"+theme+".png"))encoder.Save(output);
                model.Configure("http://127.0.0.1:1","different-account","unused");Require(model.NotificationCount==0&&!model.PeersView.Cast<Peer>().Any(),"Account switch must clear count and previous rows immediately");
            }finally{window.Close();}
        }
        Console.WriteLine("PASS: clear-search control, notification sidebar/counts, combined filters, immediate watch removal, saved views/import and account isolation.");
    }
}
