using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
internal static class ProxyChecks
{
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static async Task<string> Receive(TcpListener listener)
    {
        using var client=await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));using var stream=client.GetStream();var buffer=new byte[8192];var count=await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));var request=Encoding.ASCII.GetString(buffer,0,count);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"));return request;
    }
    public static async Task RunAsync()
    {
        foreach(var address in new[]{"socks5://localhost:1080","https://localhost:8080","http://user:secret@localhost:8080","http://localhost/path","http://localhost?x=1","http://localhost/#x","http://localhost:0","http://localhost:8080\n"}) {
            try{new HttpProxyOptions{Mode="http",Address=address}.Validate();throw new Exception("Invalid proxy accepted");}catch(ArgumentException){}
        }
        var savedProxy=HttpClient.DefaultProxy;var savedPolicy=HttpProxyPolicy.Current;
        try {
            HttpClient.DefaultProxy=new WebProxy("http://127.0.0.1:1");
            using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
            using(var client=new HttpClient(HttpProxyPolicy.CreateHandler(options:new())){Timeout=TimeSpan.FromSeconds(10)}) {
                var receive=Receive(listener);Require(await client.GetStringAsync("http://127.0.0.1:"+port+"/direct")=="OK","Direct request used dead system proxy");Require((await receive).StartsWith("GET /direct "),"Direct transport wrong request target");
            }
            var options=new HttpProxyOptions{Mode="http",Address="http://127.0.0.1:"+port};HttpProxyPolicy.Configure(options);
            using(var client=new HttpClient(HttpProxyPolicy.CreateHandler()){Timeout=TimeSpan.FromSeconds(10)}) {
                var receive=Receive(listener);Require(await client.GetStringAsync("http://destination.invalid/proxy")=="OK","Explicit proxy not used");Require((await receive).StartsWith("GET http://destination.invalid/proxy "),"Proxy did not receive absolute request target");
            }
            using(var handler=HttpProxyPolicy.CreateHandler(serverApi:true))Require(!handler.UseProxy,"API must stay direct unless opted in");
            using(var handler=HttpProxyPolicy.CreateHandler(serverApi:true,options:options with{ForServerApi=true}))Require(handler.UseProxy,"API explicit opt-in ignored");
            HttpProxyPolicy.Configure(new());using(var handler=HttpProxyPolicy.CreateHandler())Require(!handler.UseProxy,"Switching off proxy requires process restart");
            var settings=new ClientSettings{HttpProxy=options};var imported=SettingsTransfer.Import(SettingsTransfer.Export(settings));Require(imported.HttpProxy==options,"Proxy transfer not preserved");
        }finally{HttpClient.DefaultProxy=savedProxy;HttpProxyPolicy.Configure(savedPolicy);}
        if(System.Windows.Application.Current is null){var app=new VpnMonitor.Client.App(false);app.InitializeComponent();}System.Windows.Application.Current!.ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown;
        foreach(var theme in new[]{ThemeService.Dark,ThemeService.Light}) {
            ThemeService.Apply(theme);var service=new SettingsService(System.IO.Path.Combine(Environment.CurrentDirectory,"artifacts","regression","proxy-settings-"+theme));
            var window=new VpnMonitor.Client.SettingsWindow(service,new ClientSettings()){ShowInTaskbar=false,ShowActivated=false,Left=-10000,Top=-10000,Width=800,Height=760};window.Show();
            var box=(System.Windows.Controls.ComboBox)window.FindName("ProxyModeBox");var address=(System.Windows.Controls.TextBox)window.FindName("ProxyAddressBox");Require(!address.IsEnabled,"Direct mode must disable proxy address");box.SelectedIndex=1;address.Text="http://127.0.0.1:10809";
            var read=typeof(VpnMonitor.Client.SettingsWindow).GetMethod("ReadProxyOptions",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;var draft=(HttpProxyOptions)read.Invoke(window,null)!;Require(address.IsEnabled&&draft.CanonicalAddress=="http://127.0.0.1:10809/"&&!draft.ForServerApi,"Proxy form draft wrong");Require(HttpProxyPolicy.Current==savedPolicy,"Unsaved proxy draft changed active policy");
            var scroll=(System.Windows.Controls.ScrollViewer)window.FindName("SectionScroll");var sections=(System.Windows.FrameworkElement)window.FindName("SettingsSections");window.UpdateLayout();scroll.ScrollToVerticalOffset(Math.Max(0,box.TranslatePoint(new System.Windows.Point(),sections).Y-120));window.UpdateLayout();
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));System.IO.Directory.CreateDirectory("artifacts/regression/ui-0.1.78");using(var output=System.IO.File.Create("artifacts/regression/ui-0.1.78/proxy-"+theme+".png"))encoder.Save(output);window.Close();
        }
        Console.WriteLine("PASS: dead system proxy ignored, real explicit HTTP proxy, API opt-in, immediate direct switch, unsafe addresses rejected and settings transfer.");
    }
}
