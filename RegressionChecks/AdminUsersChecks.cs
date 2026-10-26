using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VpnMonitor.Client;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
internal static class AdminUsersChecks
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage,Task<HttpResponseMessage>> Respond = null!;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Respond(request);
    }
    static HttpResponseMessage Snapshot()=>new(HttpStatusCode.OK){Content=new StringContent("{\"items\":[{\"username\":\"admin\",\"is_admin\":true}],\"groups\":[],\"csrf_token\":\"fresh-token\"}",Encoding.UTF8,"application/json")};
    public static async Task RunAsync()
    {
        var calls=0;var writes=0;
        var handler=new Handler();
        handler.Respond=async request=> {
            calls++;Require(request.Headers.Authorization?.Scheme=="Basic","Basic authentication missing");
            if(request.Method==HttpMethod.Get)return Snapshot();
            writes++;Require(request.Headers.GetValues("X-VpnMonitor-CSRF").Single()=="fresh-token","Fresh CSRF missing");
            if(request.Method==HttpMethod.Patch){using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());Require(json.RootElement.EnumerateObject().Count()==1&&json.RootElement.GetProperty("password").GetString()=="new-password","Password change must not overwrite enabled state");}
            if(request.Method==HttpMethod.Delete)Require(request.RequestUri!.AbsoluteUri.Contains("%D0")&&request.RequestUri.AbsoluteUri.Contains("%2F"),"Username must be encoded as one path segment");
            return new(HttpStatusCode.NoContent);
        };
        using(var api=new AdminUsersClient("https://example.test/","admin","secret",handler)){
            await api.ChangeAsync("Офис / 1","new-password",null,default);await api.DeleteAsync("Офис / 1",default);
            Require(calls==4&&writes==2,"Each mutation must fetch current privileges and CSRF");
            try{await api.DeleteAsync("ＡＤＭＩＮ",default);throw new Exception("Master modified");}catch(AdminUsersException ex){Require(ex.Status==HttpStatusCode.Forbidden,"Master guard");}
            Require(writes==2,"Master mutation sent");
        }
        calls=0;handler=new Handler{Respond=request=>{calls++;if(request.Method==HttpMethod.Get)return Task.FromResult(Snapshot());throw new HttpRequestException("uncertain-write");}};
        using(var api=new AdminUsersClient("https://example.test","admin","secret",handler)){try{await api.CreateAsync("office","password",true,default);}catch(HttpRequestException){}Require(calls==2,"Uncertain writes must not be retried");}
        handler=new Handler{Respond=_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("password-secret-validation")})};
        using(var api=new AdminUsersClient("https://example.test","user","secret",handler)){try{await api.CreateAsync("office","password",true,default);throw new Exception("Forbidden write allowed");}catch(AdminUsersException ex){Require(!ex.ToString().Contains("password-secret"),"Server body leaked");}}
        if(Application.Current is null){var app=new App(false);app.InitializeComponent();}Application.Current!.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var fake=new Fake();var window=new UserManagementWindow(fake);
        T Find<T>(string name) where T:FrameworkElement=>(T)window.FindName(name);
        await window.RefreshAsync();var list=Find<ListBox>("UsersList");list.SelectedItem=fake.State.Items[0];Require(!Find<StackPanel>("EditorFields").IsEnabled,"Master editor enabled");
        list.SelectedItem=fake.State.Items[1];Find<PasswordBox>("PasswordBox").Password="replacement";await window.SaveAsync();Require(fake.Changes==1&&fake.Enabled is null&&fake.Password=="replacement","UI password-only mutation overwrites enabled");
        list.SelectedItem=fake.State.Items[1];Find<PasswordBox>("PasswordBox").Password="draft";fake.State=fake.State with{Items=[fake.State.Items[0],fake.State.Items[1] with{Enabled=false}]};await window.RefreshAsync();Require(Find<PasswordBox>("PasswordBox").Password=="draft"&&Find<CheckBox>("EnabledBox").IsChecked==false,"Refresh must preserve password draft and update untouched enabled state");
        fake.Fail=true;await window.SaveAsync();Require(!Find<StackPanel>("EditorFields").IsEnabled&&Find<PasswordBox>("PasswordBox").Password.Length==0,"Uncertain failure must clear secret and require refresh");window.Close();
        foreach(var theme in new[]{ThemeService.Dark,ThemeService.Light}) {
            ThemeService.Apply(theme);var view=new UserManagementWindow(new Fake()){Left=-10000,Top=-10000,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};view.Show();await view.RefreshAsync();view.UpdateLayout();
            var element=(FrameworkElement)view;var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);image.Render(element);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));System.IO.Directory.CreateDirectory("artifacts/regression/ui-0.1.76");using(var output=System.IO.File.Create("artifacts/regression/ui-0.1.76/users-"+theme+".png"))encoder.Save(output);view.Close();
        }
        Console.WriteLine("PASS: admin users fresh CSRF, master protection, selective PATCH, encoded targets, no write retry, sanitized errors and WPF editing state.");
    }
    public static async Task RunLiveAsync()
    {
        var directory=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"VpnMonitorClient");
        var service=new SettingsService(directory);
        var settings=JsonSerializer.Deserialize<ClientSettings>(System.IO.File.ReadAllText(service.SettingsPath))!;
        using var api=new AdminUsersClient(settings.ServerUrl,settings.Username,service.UnprotectPassword(settings.ProtectedPassword));
        var before=await api.ReadAsync(default);var name="desktop-check-"+Guid.NewGuid().ToString("N")[..12]+" Офис";var password=Guid.NewGuid().ToString("N");
        Require(!before.Items.Any(u=>u.Username==name),"Fixture already exists");
        try {
            await api.CreateAsync(name,password,true,default);
            using(var ordinary=new AdminUsersClient(settings.ServerUrl,name,password)) {try {await ordinary.ReadAsync(default);throw new Exception("Ordinary user accessed admin API");}catch(AdminUsersException ex){Require(ex.Status==HttpStatusCode.Forbidden,"Ordinary access denial");}}
            await api.ChangeAsync(name,null,false,default);Require(!(await api.ReadAsync(default)).Items.Single(u=>u.Username==name).Enabled,"Live block failed");
            await api.ChangeAsync(name,Guid.NewGuid().ToString("N"),true,default);Require((await api.ReadAsync(default)).Items.Single(u=>u.Username==name).Enabled,"Live enable failed");
        } finally {if((await api.ReadAsync(default)).Items.Any(u=>u.Username==name))await api.DeleteAsync(name,default);}
        var after=await api.ReadAsync(default);Require(!after.Items.Any(u=>u.Username==name),"Fixture cleanup failed");
        Require(JsonSerializer.Serialize(before.Items.OrderBy(u=>u.Username))==JsonSerializer.Serialize(after.Items.OrderBy(u=>u.Username)),"Existing account metadata changed");
        Console.WriteLine("PASS: live desktop admin API create/patch/block/enable/delete, ordinary-user denial, Unicode target and fixture cleanup; existing account metadata unchanged.");
    }
    sealed class Fake:IAdminUsersClient
    {
        public AdminUsersSnapshot State=new(){Items=[new(){Username="admin",IsAdmin=true},new(){Username="office",Enabled=true,GroupExists=true}],Groups=["office"],CsrfToken="token"};
        public int Changes;public string? Password;public bool? Enabled;public bool Fail;
        public Task<AdminUsersSnapshot> ReadAsync(CancellationToken token)=>Task.FromResult(State);
        public Task CreateAsync(string username,string password,bool enabled,CancellationToken token)=>Task.CompletedTask;
        public Task ChangeAsync(string username,string? password,bool? enabled,CancellationToken token){Changes++;Password=password;Enabled=enabled;if(Fail)throw new HttpRequestException();return Task.CompletedTask;}
        public Task DeleteAsync(string username,CancellationToken token)=>Task.CompletedTask;
        public void Dispose(){}
    }
}
