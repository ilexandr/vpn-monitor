using System.Windows;
using System.Windows.Controls;
using VpnMonitor.Client.Models;

namespace VpnMonitor.Client.Services;

public static class ClientActionMenu
{
    public static ContextMenu Create(ClientSettings settings, Func<BuiltinActionOptions, MenuItem> createBuiltin,
        Action<CustomAction> runCustom, bool usableIp)
    {
        var menu = new ContextMenu();
        var style = System.Windows.Application.Current?.TryFindResource("ActionMenuItemStyle") as Style;
        var groups = new Dictionary<string, MenuItem>(StringComparer.CurrentCultureIgnoreCase);
        void Add(string group, MenuItem item)
        {
            group = group.Trim();
            if (group.Length == 0) { menu.Items.Add(item); return; }
            if (!groups.TryGetValue(group, out var parent))
            {
                parent = new MenuItem { Header = group, Style = style };
                groups.Add(group, parent);
                menu.Items.Add(parent);
            }
            parent.Items.Add(item);
        }
        foreach (var definition in ClientActionCatalog.Resolve(settings.BuiltinActions).Where(a => a.Enabled))
            Add(definition.Group, createBuiltin(definition));
        foreach (var definition in (settings.CustomActions ?? []).Where(a => a.Enabled))
        {
            var item = new MenuItem { Header = definition.Name, Style = style, IsEnabled = usableIp };
            item.Click += (_, e) => { e.Handled = true; runCustom(definition); };
            Add(definition.Group, item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Все действия скрыты в настройках", IsEnabled = false, Style = style });
        return menu;
    }
}
