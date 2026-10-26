using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
namespace VpnMonitor.Client;
public partial class UserManagementWindow : Window
{
    private readonly IAdminUsersClient _api;
    private readonly Func<bool> _sameSession;
    private readonly CancellationTokenSource _stop=new();
    private readonly DispatcherTimer _sessionTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    private AdminUsersSnapshot _snapshot=new();
    private AdminUser? _selected;
    private bool _busy,_mutating,_closed,_closeRequested,_invalidated,_updating,_loaded,_gone;
    public UserManagementWindow(IAdminUsersClient api,Func<bool>? sameSession=null)
    {
        _api=api;_sameSession=sameSession??(()=>true);InitializeComponent();
        SourceInitialized+=(_,_)=>ThemeService.ApplyWindowChrome(this);
        Loaded+=async(_,_)=>{_sessionTimer.Start();await RefreshAsync();};
        _sessionTimer.Tick+=(_,_)=> {if(!_sameSession())InvalidateSession();};
        NameBox.TextChanged+=(_,_)=>UpdateGroupHint();
        Closing+=(_,e)=> {
            if(_mutating&&!_invalidated){_closeRequested=true;e.Cancel=true;StatusText.Text="Окно закроется после завершения запроса. Повторная отправка не выполняется.";}
        };
        Closed+=(_,_)=>{_closed=true;_stop.Cancel();_sessionTimer.Stop();PasswordBox.Clear();_snapshot=new();_selected=null;UsersList.ItemsSource=null;GroupChoices.ItemsSource=null;NameBox.Clear();_api.Dispose();};
        ResetEditor();SetControls();
    }
    public Task RefreshAsync()=>RunAsync(async token=>ApplySnapshot(await _api.ReadAsync(token),true),false);
    private void ApplySnapshot(AdminUsersSnapshot snapshot,bool keepDraft)
    {
        if(_closed)return;
        if(!_sameSession()){InvalidateSession();return;}
        var previous=_selected;
        var editedEnabled=previous is not null && EnabledBox.IsChecked!=previous.Enabled;
        var fresh=previous is null?null:snapshot.Items.FirstOrDefault(u=>u.Username==previous.Username);
        if(keepDraft&&fresh is not null&&previous is not null) {
            _selected=fresh with {Enabled=editedEnabled?previous.Enabled:fresh.Enabled};
            if(!editedEnabled)EnabledBox.IsChecked=fresh.Enabled;
            AdminHint.Visibility=fresh.IsAdmin?Visibility.Visible:Visibility.Collapsed;
        }
        _snapshot=snapshot with {CsrfToken=""};_loaded=true;
        _updating=true;
        try {
            UsersList.ItemsSource=snapshot.Items;
            UsersList.SelectedItem=_selected is null ? null : snapshot.Items.FirstOrDefault(u=>u.Username==_selected.Username);
            GroupChoices.ItemsSource=snapshot.Groups;
            _gone=_selected is not null && UsersList.SelectedItem is null;
            if(!keepDraft)ResetEditor();
        } finally {_updating=false;}
        UpdateGroupHint();StatusText.Text=_gone?"Аккаунт удалён на сервере. Выберите другой или создайте новый.":$"Аккаунтов: {snapshot.Items.Length}. Изменения применяются на сервере сразу.";
    }
    private void ResetEditor()
    {
        _selected=null;_gone=false;_updating=true;
        UsersList.SelectedItem=null;GroupChoices.SelectedItem=null;_updating=false;
        NameBox.Text="";NameBox.IsReadOnly=false;PasswordBox.Clear();EnabledBox.IsChecked=true;
        EditorTitle.Text="Новый пользователь";SaveButton.Content="Создать";PasswordHint.Text="Для нового аккаунта задайте пароль.";
        AdminHint.Visibility=Visibility.Collapsed;UpdateGroupHint();SetControls();
    }
    private void SetControls()
    {
        RefreshButton.IsEnabled=!_busy;NewButton.IsEnabled=_loaded&&!_busy;UsersList.IsEnabled=!_busy;
        EditorFields.IsEnabled=_loaded&&!_busy&&!_gone&&_selected?.IsAdmin!=true;
        DeleteButton.IsEnabled=_loaded&&!_busy&&!_gone&&_selected is {IsAdmin:false};
        GroupChoices.IsEnabled=_selected is null;
    }
    private void User_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_updating||UsersList.SelectedItem is not AdminUser user)return;
        _selected=user;_gone=false;NameBox.Text=user.Username;NameBox.IsReadOnly=true;PasswordBox.Clear();EnabledBox.IsChecked=user.Enabled;
        EditorTitle.Text=user.IsAdmin?"Администратор":"Изменить пользователя";SaveButton.Content="Сохранить";
        PasswordHint.Text="Пустой пароль оставляет прежний. Изменяйте только необходимые параметры.";
        AdminHint.Visibility=user.IsAdmin?Visibility.Visible:Visibility.Collapsed;UpdateGroupHint();SetControls();
    }
    private void UpdateGroupHint()
    {
        if(GroupHint is null)return;
        var name=NameBox.Text;
        GroupHint.Text=string.IsNullOrWhiteSpace(name)?"Имя можно ввести вручную или выбрать из списка групп.":
            _selected is {IsAdmin:true}?"Доступ ко всем группам.":
            _selected is not null ? (_selected.GroupExists?"Группа найдена на сервере.":"Группа не найдена на сервере; список клиентов пуст."):
            _snapshot.Groups.Any(g=>g==name)?"Группа найдена.":"Если группы с таким именем нет, список клиентов будет пуст.";
    }
    private void Group_Changed(object sender,SelectionChangedEventArgs e){if(!_updating&&_selected is null&&GroupChoices.SelectedItem is string name)NameBox.Text=name;}
    private void New_Click(object sender,RoutedEventArgs e){if(!_busy&&_loaded)ResetEditor();}
    private void Reset_Click(object sender,RoutedEventArgs e){if(!_busy)ResetEditor();}
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshAsync();
    private async void Save_Click(object sender,RoutedEventArgs e)=>await SaveAsync();
    public async Task SaveAsync()
    {
        if(_busy||!_loaded||_gone||_selected?.IsAdmin==true)return;
        var original=_selected;var name=original?.Username??NameBox.Text.Trim();var password=PasswordBox.Password;var enabled=EnabledBox.IsChecked==true;
        if(string.IsNullOrWhiteSpace(name)||name.Length>80||name.Contains(':')||name.Any(c=>char.IsControl(c)||char.GetUnicodeCategory(c)==UnicodeCategory.Format)) {StatusText.Text="Проверьте имя: 1–80 символов, без двоеточия и управляющих символов.";return;}
        if(original is null&&password.Length==0){StatusText.Text="Задайте пароль для нового аккаунта.";return;}
        bool? changedEnabled=original is null||original.Enabled!=enabled?enabled:null;
        if(original is not null&&password.Length==0&&changedEnabled is null){StatusText.Text="Нет изменений для сохранения.";return;}
        PasswordBox.Clear();
        await RunAsync(async token=> {
            if(original is null)await _api.CreateAsync(name,password,enabled,token);
            else await _api.ChangeAsync(name,password.Length==0?null:password,changedEnabled,token);
            var updated=await _api.ReadAsync(token);ApplySnapshot(updated,false);StatusText.Text="Сохранено на сервере.";
        },true);
    }
    private async void Delete_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||!_loaded||_gone||_selected is not {IsAdmin:false} user)return;
        if(ThemedMessageBox.Show(this,$"Удалить учётную запись «{user.Username}»? Группа и VPN-клиенты сохранятся.","Удаление пользователя",MessageBoxButton.OKCancel,MessageBoxImage.Warning,MessageBoxResult.Cancel,"Удалить")!=MessageBoxResult.OK)return;
        await RunAsync(async token=>{await _api.DeleteAsync(user.Username,token);ApplySnapshot(await _api.ReadAsync(token),false);StatusText.Text="Пользователь удалён.";},true);
    }
    private async Task RunAsync(Func<CancellationToken,Task> operation,bool mutation)
    {
        if(_busy||_closed)return;
        if(!_sameSession()){InvalidateSession();return;}
        _busy=true;_mutating=mutation;SetControls();StatusText.Text=mutation?"Сохранение на сервере…":"Загрузка списка…";
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try {await operation(deadline.Token);}
        catch(Exception ex) {
            if(_closed)return;
            _loaded=false;
            if(ex is AdminUsersException {Status:HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden}) {UsersList.ItemsSource=null;_snapshot=new();ResetEditor();}
            PasswordBox.Clear();
            StatusText.Text=ex is AdminUsersException known?known.Message:
                mutation?"Результат запроса не подтверждён. Обновите список перед повтором; запрос автоматически не повторялся.":"Не удалось загрузить список. Проверьте соединение и обновите снова.";
        }
        finally {
            _busy=false;_mutating=false;
            if(!_closed){SetControls();if(_closeRequested)Close();}
        }
    }
    private void InvalidateSession(){_invalidated=true;_stop.Cancel();PasswordBox.Clear();UsersList.ItemsSource=null;Close();}
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
