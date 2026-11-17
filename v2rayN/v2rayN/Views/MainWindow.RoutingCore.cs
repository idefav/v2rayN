using System.Windows.Controls;
using ServiceLib.Services;

namespace v2rayN.Views;

public partial class MainWindow
{
    private RoutingCoreChoice ShowRoutingCorePrompt(RoutingCorePrompt prompt)
    {
        var choice = RoutingCoreChoice.Cancel;
        var dialog = new Window
        {
            Owner = this, Title = RoutingCoreCompatibility.T("路由与核心不兼容", "Routing / core compatibility"),
            Width = 620, SizeToContent = SizeToContent.Height, MaxHeight = 650,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = prompt.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) }
        });
        void Add(string label, RoutingCoreChoice result, bool enabled = true)
        {
            var button = new Button { Content = label, IsEnabled = enabled, Margin = new Thickness(0, 4, 0, 4) };
            button.Click += (_, _) => { choice = result; dialog.Close(); };
            content.Children.Add(button);
        }
        Add(RoutingCoreCompatibility.T("使用 sing-box 并连接", "Use sing-box and connect"), RoutingCoreChoice.UseSingbox, prompt.CanUseSingbox);
        Add(RoutingCoreCompatibility.T("编辑冲突规则", "Edit conflicting rule"), RoutingCoreChoice.EditRule);
        Add(RoutingCoreCompatibility.T("取消", "Cancel"), RoutingCoreChoice.Cancel);
        dialog.Content = content;
        if (prompt.CancellationToken.IsCancellationRequested) return choice;
        if (!IsVisible) ShowHideWindow(true);
        using var registration = prompt.CancellationToken.Register(() => dialog.Dispatcher.BeginInvoke(new Action(dialog.Close)));
        dialog.ShowDialog();
        return choice;
    }
}
