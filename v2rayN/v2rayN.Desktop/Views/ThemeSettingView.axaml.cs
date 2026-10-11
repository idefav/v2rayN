using Avalonia.Data;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// ThemeSettingView.xaml
/// </summary>
public partial class ThemeSettingView : ReactiveUserControl<ThemeSettingViewModel>
{
    public ThemeSettingView()
    {
        InitializeComponent();
        ViewModel = new ThemeSettingViewModel();
        var previews = new Dictionary<string, Button>();
        foreach (var (theme, label, dark) in new[]
        {
            (nameof(ETheme.Light), Common.WorkspaceText.T("浅色", "Light"), false),
            (nameof(ETheme.Dark), Common.WorkspaceText.T("深色", "Dark"), true),
            (nameof(ETheme.FollowSystem), Common.WorkspaceText.T("跟随系统", "System"), false)
        })
        {
            var preview = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*"), Height = 80 };
            preview.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse(dark ? "#1B222D" : "#F3F5F8")), CornerRadius = new CornerRadius(6, 0, 0, 6) });
            var page = new Border { Background = new SolidColorBrush(Color.Parse(dark ? "#202733" : "#FFFFFF")), Padding = new Thickness(10), CornerRadius = new CornerRadius(0, 6, 6, 0) };
            Grid.SetColumn(page, 1);
            page.Child = new StackPanel { Spacing = 8, Children = { new Border { Background = new SolidColorBrush(Color.Parse("#1677FF")), Width = 72, Height = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left }, new Border { Background = new SolidColorBrush(Color.Parse(dark ? "#364152" : "#E8EBEF")), Height = 24 } } };
            preview.Children.Add(page);
            var choice = new Button { Width = 192, Padding = new Thickness(8), Margin = new Thickness(0, 0, 12, 8), Content = new StackPanel { Spacing = 8, Children = { preview, new TextBlock { Text = label, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center } } } };
            choice.Click += (_, _) => ViewModel.CurrentTheme = theme;
            previews.Add(theme, choice);
            themeChoices.Children.Add(choice);
        }

        cmbCurrentTheme.ItemsSource = Utils.GetEnumNames<ETheme>().Select(name => new LanguageOption(name,
            name == nameof(ETheme.FollowSystem) ? Common.WorkspaceText.T("跟随系统", "Follow system") : name)).ToList();
        cmbCurrentTheme.DisplayMemberBinding = new Binding(nameof(LanguageOption.Display));
        cmbCurrentTheme.SelectedValueBinding = new Binding(nameof(LanguageOption.Value));
        cmbCurrentFontSize.ItemsSource = Enumerable.Range(Global.MinFontSize, Global.MinFontSizeCount).ToList();
        cmbCurrentLanguage.ItemsSource = Global.LanguageOptions;
        cmbCurrentLanguage.DisplayMemberBinding = new Binding(nameof(LanguageOption.Display));
        cmbCurrentLanguage.SelectedValueBinding = new Binding(nameof(LanguageOption.Value));

        this.WhenActivated(disposables =>
        {
            this.WhenAnyValue(v => v.ViewModel.CurrentTheme).Subscribe(theme =>
            {
                foreach (var (value, button) in previews) button.BorderBrush = value == theme ? new SolidColorBrush(Color.Parse("#1677FF")) : null;
            }).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.CurrentTheme, v => v.cmbCurrentTheme.SelectedValue).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.CurrentFontSize, v => v.cmbCurrentFontSize.SelectedValue).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.CurrentLanguage, v => v.cmbCurrentLanguage.SelectedValue).DisposeWith(disposables);
        });
    }
}
