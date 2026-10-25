using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VpnMonitor.Client.Services;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
namespace VpnMonitor.Client;

public sealed class AwgProfileEditorWindow : Window
{
    public string? Configuration { get; private set; }
    public AwgProfileEditorWindow(string name, string config, bool canSave)
    {
        Title = "Конфигурация — " + name; Width = 760; Height = 700; MinWidth = 520; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; SetResourceReference(StyleProperty, typeof(Window));
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
        var document = new AwgEditableConfig(config);
        var root = new DockPanel { Margin = new(22) };
        var footer = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new(0, 14, 0, 0) };
        var save = new Button { Content = "Сохранить", IsEnabled = canSave, Padding = new(14, 8, 14, 8) }; save.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        footer.Children.Add(save); footer.Children.Add(new Button { Content = "Отмена", IsCancel = true, Padding = new(14, 8, 14, 8), Margin = new(8, 0, 0, 0) });
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var info = new TextBlock { Text = canSave
            ? "Все параметры .conf доступны для редактирования. Метки <скрыто:…> сохраняют прежние ключи; для замены ключа введите новое значение вместо метки. Проверка движком выполняется перед сохранением. Изменения применятся при следующем подключении."
            : "Просмотр конфигурации. Для редактирования сначала отключите общий туннель. Ключи скрыты.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) };
        info.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(info, Dock.Top); root.Children.Add(info);
        var box = new TextBox { Text = document.Text, IsReadOnly = !canSave, AcceptsReturn = true, AcceptsTab = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 256 * 1024, Padding = new(12), UndoLimit = 30 };
        root.Children.Add(box); Content = root;
        save.Click += (_, _) =>
        {
            try { var value = document.Restore(box.Text); _ = AwgConfigPreview.Parse(value); Configuration = value; DialogResult = true; }
            catch (ArgumentException ex) { ThemedMessageBox.Show(this, ex.Message, "Конфигурация", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        Closed += (_, _) => box.Clear();
    }
}
