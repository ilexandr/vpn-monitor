using System.Windows;
using System.Windows.Controls;

namespace VpnMonitor.Client.Services;

public static class NetworkShareMenu
{
    public static MenuItem CreateSubmenu(Action<string?> open, Action choose, bool enabled)
    {
        var item = new MenuItem { Header = "Общие папки", IsEnabled = enabled };
        item.Style = System.Windows.Application.Current?.TryFindResource("ActionMenuItemStyle") as Style;
        Populate(item, open, choose);
        return item;
    }

    public static ContextMenu CreateContextMenu(Action<string?> open, Action choose)
    {
        var menu = new ContextMenu();
        Populate(menu, open, choose);
        return menu;
    }

    private static void Populate(ItemsControl parent, Action<string?> open, Action choose)
    {
        Add(parent, "Все общие папки", () => open(null));
        Add(parent, "Диск C (C$)", () => open("C$"));
        Add(parent, "Windows (ADMIN$)", () => open("ADMIN$"));
        Add(parent, "Другая общая папка...", choose);
    }

    private static void Add(ItemsControl parent, string text, Action action)
    {
        var item = new MenuItem
        {
            Header = text,
            Style = System.Windows.Application.Current?.TryFindResource("ActionMenuItemStyle") as Style
        };
        item.Click += (_, _) => action();
        parent.Items.Add(item);
    }
}