using VpnMonitor.Client.Models;
using VpnMonitor.Client.ViewModels;

namespace VpnMonitor.Client.Services;

public static class SavedViewService
{
    public static IEnumerable<SavedClientView> Visible(ClientSettings settings, string scope, MainViewModel model) =>
        (settings.SavedViews ?? []).Where(v => model.AwgState.IsCurrent && v.Scope == scope && (v.Group is null || model.Groups.Any(g => g.Name == v.Group)) &&
            (!model.IsRestrictedUser || v.Group is null || v.Group == model.RestrictedGroup));
    public static SavedClientView Capture(string name, string scope, MainViewModel model, ClientSettings settings) => new()
    {
        Name = name.Trim(), Scope = scope, Search = model.SearchText, Group = model.SelectedGroup, Filter = model.Filter,
        FavoritesOnly = model.FavoritesOnly, SortMember = model.SortMember, SortDescending = model.SortDescending,
        Status = settings.ShowStatusColumn, Computer = settings.ShowComputerColumn, VpnIp = settings.ShowVpnIpColumn,
        Handshake = settings.ShowHandshakeColumn, Traffic = settings.ShowTrafficColumn, Groups = settings.ShowGroupsColumn
    };
    public static bool Apply(SavedClientView view, string scope, MainViewModel model, ClientSettings settings)
    {
        if (!model.AwgState.IsCurrent || !Visible(new() { SavedViews = [view] }, scope, model).Any()) return false;
        model.SearchText = view.Search;
        model.SelectedGroup = model.IsRestrictedUser ? model.RestrictedGroup : view.Group;
        model.Filter = view.Filter; model.FavoritesOnly = view.FavoritesOnly;
        model.ApplySort(view.SortMember, view.SortDescending);
        settings.ShowStatusColumn = view.Status; settings.ShowComputerColumn = view.Computer; settings.ShowVpnIpColumn = view.VpnIp;
        settings.ShowHandshakeColumn = view.Handshake; settings.ShowTrafficColumn = view.Traffic; settings.ShowGroupsColumn = view.Groups;
        settings.SelectedGroup = model.SelectedGroup; settings.Filter = view.Filter; settings.FavoritesOnly = view.FavoritesOnly;
        settings.SortMember = view.SortMember; settings.SortDescending = view.SortDescending;
        return true;
    }
}
