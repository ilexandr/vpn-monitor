using System.Text.Json;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

internal sealed class ManagerForm : System.Windows.Forms.Form
{
    private readonly Label _status=new(){AutoSize=true,MaximumSize=new System.Drawing.Size(620,0)};
    private readonly ListBox _list=new(){Dock=DockStyle.Fill,DisplayMember="Name"};
    private readonly FlowLayoutPanel _buttons=new(){Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};
    private readonly Label _result=new(){Dock=DockStyle.Fill,AutoSize=true};
    private readonly System.Windows.Forms.Timer _timer=new(){Interval=2000};
    private bool _busy;
    private string _serialized="";
    private AwgProfileCatalog _catalog=new();
    private static string CatalogPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"VpnMonitor","AwgStatus","profiles.json");
    internal ManagerForm()
    {
        Text="VPN Monitor — управление общими туннелями";
        Width=720;Height=520;MinimumSize=new System.Drawing.Size(640,440);
        StartPosition=FormStartPosition.CenterScreen;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=1,RowCount=5};
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_status,0,0);
        layout.Controls.Add(new Label{AutoSize=true,MaximumSize=new System.Drawing.Size(620,0),Padding=new Padding(0,10,0,10),
            Text="Туннель общий для всех пользователей. Выход из панели не отключает VPN. Перед выбором другого профиля отключите службу. Добавленный при работающем VPN профиль не переключает текущее подключение."},0,1);
        layout.Controls.Add(_list,0,2);layout.Controls.Add(_buttons,0,3);layout.Controls.Add(_result,0,4);Controls.Add(layout);
        Add("Добавить .conf",async()=>{
            using var dialog=new OpenFileDialog{Filter="Профили AWG (*.conf)|*.conf"};
            if(dialog.ShowDialog(this)==DialogResult.OK)await Run("import",dialog.FileName);
        });
        Add("Выбрать",async()=>{if(_list.SelectedItem is AwgProfileInfo p)await Run("select",p.Id);});
        Add("Удалить",async()=>{if(_list.SelectedItem is AwgProfileInfo p)await Run("remove",p.Id);});
        Add("Подключить",async()=>await Run("start","-"));
        Add("Отключить",async()=>await Run("stop","-"));
        _timer.Tick+=(_,_)=>RefreshState();
        Shown+=(_,_)=>{RefreshState();_timer.Start();};
        FormClosing+=(_,e)=>{if(_busy)e.Cancel=true;};
        FormClosed+=(_,_)=>_timer.Dispose();
    }
    private void Add(string text,Func<Task> action)
    {
        var button=new Button{Text=text,AutoSize=true,Padding=new Padding(5),Margin=new Padding(0,10,8,6)};
        button.Click+=async(_,_)=>{try{await action();}catch{_result.Text="Ошибка операции. Профиль и ключи не выводятся.";}};_buttons.Controls.Add(button);
    }
    private void RefreshState()
    {
        try
        {
            var json=File.ReadAllText(CatalogPath);
            if(json!=_serialized)
            {
                var previous=(_list.SelectedItem as AwgProfileInfo)?.Id;
                _catalog=JsonSerializer.Deserialize<AwgProfileCatalog>(json)!;_serialized=json;
                _list.DataSource=_catalog.Profiles;
                _list.SelectedItem=_catalog.Profiles.FirstOrDefault(p=>p.Id==(previous??_catalog.SelectedId));
            }
            var selected=_catalog.Profiles.FirstOrDefault(p=>p.Id==_catalog.SelectedId)?.Name??"не выбран";
            _status.Text=$"Служба: {AwgServiceStatus.Read().Text}\nВыбран для подключения: {selected}";
        }
        catch{_status.Text="Не удалось прочитать общие настройки.";}
    }
    private async Task Run(string action,string argument)
    {
        if(_busy)return;
        var name=(_list.SelectedItem as AwgProfileInfo)?.Name??"";
        var text=action switch{"import"=>"Добавить общий профиль из выбранного файла?","select"=>$"Выбрать «{name}» для подключения?","remove"=>$"Удалить «{name}»?","start"=>"Подключить общий VPN для всех пользователей?",_=>"Отключить VPN у всех пользователей компьютера?"};
        if(MessageBox.Show(this,text,"Общий туннель",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning)!=DialogResult.OK)return;
        _busy=true;_buttons.Enabled=false;_list.Enabled=false;_result.Text="Выполнение...";
        try
        {
            var code=await Task.Run(()=>Program.Execute(action,argument));
            _result.Text=code switch{0=>"Готово.",12=>"Сначала остановите туннель; профиль ещё не выбран или служба занята.",13=>"Выбранный профиль удалить нельзя — сначала выберите другой.",14=>"Конфликт маршрутов: подключение не запущено.",15=>"Файл занят, повторите позже.",16=>"Операция не выполнена: проверьте файл профиля.",17=>"Служба не достигла нужного состояния. Проверьте журнал.",_=>$"Запрос не выполнен (код {code})."};
        }
        finally{_busy=false;_buttons.Enabled=true;_list.Enabled=true;RefreshState();}
    }
}