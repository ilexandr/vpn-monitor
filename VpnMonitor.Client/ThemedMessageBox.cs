using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VpnMonitor.Client.Services;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace VpnMonitor.Client;

public sealed class ThemedMessageBox : Window
{
    public MessageBoxResult Result { get; private set; }
    public ThemedMessageBox(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None,
        string? acceptText = null)
    {
        Title = title; Width = 500; SizeToContent = SizeToContent.Height; MaxHeight = 650;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 14) };
        root.Children.Add(heading);
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 430 };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new(0, 24, 0, 0) };
        Grid.SetRow(actions, 2); root.Children.Add(actions);
        var yesNo = buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel;
        var accept = yesNo ? MessageBoxResult.Yes : MessageBoxResult.OK;
        var dismiss = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
        Result = dismiss;
        void Add(string label, MessageBoxResult result, bool primary = false)
        {
            var button = new Button { Content = label, MinWidth = 96, Padding = new(14, 8, 14, 8), Margin = new(8, 0, 0, 0),
                IsCancel = result == dismiss, IsDefault = result == (defaultResult == MessageBoxResult.None ? dismiss : defaultResult) };
            if (primary) button.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
            button.Click += (_, _) => { Result = result; DialogResult = result == accept; };
            actions.Children.Add(button);
        }
        Add(acceptText ?? (yesNo ? "Да" : buttons == MessageBoxButton.OK ? "Закрыть" : "Продолжить"), accept, true);
        if (buttons == MessageBoxButton.YesNoCancel) Add("Нет", MessageBoxResult.No);
        if (buttons != MessageBoxButton.OK) Add(buttons == MessageBoxButton.YesNo ? "Нет" : "Отмена", dismiss);
        var surface = new Border { Child = root, Padding = new Thickness(24) };
        surface.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        Content = surface;
    }
    public static MessageBoxResult Show(Window owner, string message, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None, string? acceptText = null)
    {
        var dialog = OwnedWindowBehavior.Attach(new ThemedMessageBox(message, title, buttons, image, defaultResult, acceptText), owner);
        dialog.ShowDialog(); return dialog.Result;
    }
}
