using Avalonia.Layout;
using Avalonia.Media;
using ServiceLib.Services;

namespace v2rayN.Desktop.Views;

public partial class MainWindow
{
    internal async Task<RoutingCoreChoice> ShowRoutingCorePromptAsync(RoutingCorePrompt prompt)
    {
        var dialog = new Window
        {
            Title = RoutingCoreCompatibility.T("路由与核心不兼容", "Routing / core compatibility"),
            Width = 620, SizeToContent = SizeToContent.Height, MaxHeight = 650,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = true
        };
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 400,
            Content = new TextBlock { Text = prompt.Message, TextWrapping = TextWrapping.Wrap }
        });
        var actions = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        void Add(string label, RoutingCoreChoice choice, bool enabled = true)
        {
            var button = new Button { Content = label, IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => dialog.Close(choice);
            actions.Children.Add(button);
        }
        Add(RoutingCoreCompatibility.T("使用 sing-box 并连接", "Use sing-box and connect"), RoutingCoreChoice.UseSingbox, prompt.CanUseSingbox);
        Add(RoutingCoreCompatibility.T("编辑冲突规则", "Edit conflicting rule"), RoutingCoreChoice.EditRule);
        Add(RoutingCoreCompatibility.T("取消", "Cancel"), RoutingCoreChoice.Cancel);
        content.Children.Add(actions);
        dialog.Content = content;
        if (prompt.CancellationToken.IsCancellationRequested) return RoutingCoreChoice.Cancel;
        var owner = Manager.WindowDialog.TryGetOwnerWindow();
        if (!owner.IsVisible) { ShowHideWindow(true); owner = this; }
        using var registration = prompt.CancellationToken.Register(() =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Close(RoutingCoreChoice.Cancel)));
        return await dialog.ShowDialog<RoutingCoreChoice>(owner);
    }
}
