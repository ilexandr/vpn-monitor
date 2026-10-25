using System.Windows;
using System.Windows.Controls;
using VpnMonitor.Client.Services;
using Button=System.Windows.Controls.Button;
namespace VpnMonitor.Client;
public sealed class UpdateFilePlanWindow : Window
{
    private readonly CancellationTokenSource _stop=new();
    public UpdateFilePlanWindow(UpdateRelease release,Func<CancellationToken,Task<UpdateFilePlan>>? load=null)
    {
        Title="Состав версии — "+release.Version;Width=760;Height=560;MinWidth=520;MinHeight=350;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;SetResourceReference(StyleProperty,typeof(Window));
        SourceInitialized+=(_,_)=>ThemeService.ApplyWindowChrome(this);Closed+=(_,_)=>_stop.Cancel();
        var root=new DockPanel {Margin=new(22)};var close=new Button {Content="Закрыть",IsCancel=true,Padding=new(14,8,14,8),HorizontalAlignment=System.Windows.HorizontalAlignment.Right,Margin=new(0,12,0,0)};
        close.Click+=(_,_)=>Close();DockPanel.SetDock(close,Dock.Bottom);root.Children.Add(close);
        var status=new TextBlock {Text="Проверка манифеста GitHub и установленных файлов…",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12)};
        DockPanel.SetDock(status,Dock.Top);root.Children.Add(status);
        var note=new TextBlock {Text="Это оценка объёма без сжатия. Сейчас обновление использует полный установщик. Файлы и работающий туннель при оценке не изменяются.",TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,0)};
        note.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");DockPanel.SetDock(note,Dock.Bottom);root.Children.Add(note);
        var list=new System.Windows.Controls.ListBox();root.Children.Add(list);Content=root;
        Loaded+=async(_,_)=> {
            try {
                var plan=await (load?.Invoke(_stop.Token) ?? UpdateService.GetFilePlanAsync(release,UpdateParticipant.InstallRoot,_stop.Token));
                if(_stop.IsCancellationRequested)return;
                status.Text=$"Изменилось файлов: {plan.Changed.Length}. Совпадает: {plan.Unchanged}.\nИзменения: {plan.ChangedBytes/1048576d:F2} МБ из {plan.TotalBytes/1048576d:F2} МБ без сжатия.";
                list.ItemsSource=plan.Changed.Select(f=>$"{f.Path}  ·  {f.Size/1024d:F1} КБ").ToArray();
            } catch(OperationCanceledException) {if(!_stop.IsCancellationRequested)status.Text="Превышено время ожидания.";}
            catch(Exception) {if(!_stop.IsCancellationRequested)status.Text="Не удалось проверить состав обновления. Повторите позже; обычное обновление полным установщиком остаётся доступным.";}
        };
    }
}
