using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace VpnMonitor.Client.Services;

// Keep native notification-area focus, keyboard dismissal and per-monitor DPI handling.
public static class TrayMenuTheme
{
    public static void Apply(Forms.ContextMenuStrip menu)
    {
        Drawing.Color Color(string key)
        {
            var brush = (SolidColorBrush)System.Windows.Application.Current.FindResource(key);
            var c = brush.Color; return Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        var panel = Color("PanelBrush"); var text = Color("TextBrush");
        menu.BackColor = panel; menu.ForeColor = text;
        menu.Renderer = new Forms.ToolStripProfessionalRenderer(new Palette(panel, Color("RowHoverBrush"), Color("BorderBrush")));
        foreach (Forms.ToolStripItem item in menu.Items)
        { item.ForeColor = text; item.Padding = new Forms.Padding(8, 6, 8, 6); }
    }
    private sealed class Palette(Drawing.Color panel, Drawing.Color hover, Drawing.Color border) : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => panel;
        public override Drawing.Color MenuItemSelected => hover;
        public override Drawing.Color MenuItemBorder => border;
        public override Drawing.Color MenuBorder => border;
        public override Drawing.Color SeparatorDark => border;
        public override Drawing.Color SeparatorLight => panel;
        public override Drawing.Color ImageMarginGradientBegin => panel;
        public override Drawing.Color ImageMarginGradientMiddle => panel;
        public override Drawing.Color ImageMarginGradientEnd => panel;
    }
}
