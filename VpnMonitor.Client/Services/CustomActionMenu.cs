using System.Windows;
using System.Windows.Controls;
using VpnMonitor.Client.Models;
namespace VpnMonitor.Client.Services;
public static class CustomActionMenu
{
    public static MenuItem Create(IEnumerable<CustomAction> actions, Action<CustomAction> run, bool enabled)
    {
        var style = System.Windows.Application.Current?.TryFindResource("ActionMenuItemStyle") as Style;
        var root = new MenuItem { Header = "Пользовательские действия", IsEnabled = enabled, Style = style };
        var groups = new Dictionary<string, MenuItem>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var action in actions.Where(a => a.Enabled))
        {
            var group = action.Group?.Trim() ?? "";
            var parent = root;
            if (group.Length > 0)
            {
                if (!groups.TryGetValue(group, out parent))
                {
                    parent = new MenuItem { Header = group, Style = style };
                    groups.Add(group, parent);
                    root.Items.Add(parent);
                }
            }
            var item = new MenuItem { Header = action.Name, Style = style };
            item.Click += (_, _) => run(action);
            parent.Items.Add(item);
        }
        return root;
    }
}
