using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
namespace VpnMonitor.Client;
public partial class AwgConnectionHistoryWindow : Window
{
    public sealed record HistoryScope(string? Id,string Title);
    private readonly Func<AwgConnectionEvent[]> _read;
    private readonly Func<string,AwgConnectionEvent[]> _readProfile;
    private readonly Func<AwgProfileCatalog> _catalog;
    private readonly string? _initial;
    private bool _updating,_initialized;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    public AwgConnectionHistoryWindow(Func<AwgConnectionEvent[]>? read = null,string? selectedProfileId=null,Func<AwgProfileCatalog>? catalog=null,Func<string,AwgConnectionEvent[]>? readProfile=null)
    {
        _read=read??(()=>AwgConnectionHistory.Read());_initial=selectedProfileId;
        _catalog=catalog??(read is null?AwgProfileCatalogReader.Read:()=>new());
        _readProfile=readProfile??(read is null?id=>AwgConnectionHistory.Read(profileId:id):id=>_read().Where(e=>e.ProfileId==id).ToArray());
        InitializeComponent();SourceInitialized+=(_,_)=>ThemeService.ApplyWindowChrome(this);
        Loaded+=(_,_)=>{Refresh();_timer.Start();};_timer.Tick+=(_,_)=>Refresh();Closed+=(_,_)=>_timer.Stop();
    }
    public void Refresh()
    {
        var all=_read();AwgProfileCatalog catalog;try{catalog=_catalog();}catch{catalog=new();}
        var requested=_initialized?(ProfileScopeBox.SelectedItem as HistoryScope)?.Id:_initial;
        var scopes=new List<HistoryScope>{new(null,"Все профили")};
        foreach(var p in catalog.Profiles??[])if(p is not null&&Guid.TryParseExact(p.Id,"N",out _)&&!scopes.Any(s=>s.Id==p.Id))scopes.Add(new(p.Id,p.Name is {Length:>0 and <=80}?p.Name:"Профиль "+p.Id[..8]));
        foreach(var item in all.Where(e=>!string.IsNullOrEmpty(e.ProfileId)).DistinctBy(e=>e.ProfileId))
            if(!scopes.Any(s=>s.Id==item.ProfileId))scopes.Add(new(item.ProfileId,item.ProfileText+" (нет в списке профилей)"));
        scopes.Add(new("","Без привязки к профилю"));
        if(requested is {Length:32}&&!scopes.Any(s=>s.Id==requested)&&Guid.TryParseExact(requested,"N",out _))scopes.Add(new(requested,"Профиль "+requested[..8]));
        _updating=true;
        try {
            if(ProfileScopeBox.ItemsSource is not IEnumerable<HistoryScope> oldScopes||!oldScopes.SequenceEqual(scopes))ProfileScopeBox.ItemsSource=scopes;
            ProfileScopeBox.SelectedItem=ProfileScopeBox.Items.Cast<HistoryScope>().FirstOrDefault(s=>s.Id==requested)??ProfileScopeBox.Items[0];
        }finally{_updating=false;_initialized=true;}
        var id=(ProfileScopeBox.SelectedItem as HistoryScope)?.Id;
        var rows=id is null?all:id.Length==0?all.Where(e=>string.IsNullOrEmpty(e.ProfileId)).ToArray():_readProfile(id).Where(e=>e.ProfileId==id).ToArray();
        if(HistoryGrid.ItemsSource is not IEnumerable<AwgConnectionEvent> old||!old.SequenceEqual(rows)) {
            var selected=HistoryGrid.SelectedItem as AwgConnectionEvent;HistoryGrid.ItemsSource=rows;HistoryGrid.SelectedItem=rows.FirstOrDefault(e=>e==selected);
        }
        SummaryText.Text=rows.Length==0?"Событий не найдено. Для записи по профилям нужен движок 0.1.80 или новее.":$"Событий: {rows.Length}. Обновление каждые 2 с.";
    }
    private void ProfileScope_Changed(object sender,SelectionChangedEventArgs e){if(!_updating)Refresh();}
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
