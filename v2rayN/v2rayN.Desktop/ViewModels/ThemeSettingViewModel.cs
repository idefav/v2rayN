using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using AvaloniaEdit;
using Semi.Avalonia;

namespace v2rayN.Desktop.ViewModels;

public partial class ThemeSettingViewModel : MyReactiveObject
{
    [Reactive] public partial string CurrentTheme { get; set; }

    [Reactive] public partial int CurrentFontSize { get; set; }

    [Reactive] public partial string CurrentLanguage { get; set; }

    [Reactive] public partial string CurrentFontFamily { get; set; }
    public bool IsDraft { get; }
    private static Style? _fontStyle;

    public ThemeSettingViewModel(bool editAsDraft = false)
    {
        _config = AppManager.Instance.Config;
        IsDraft = editAsDraft;
        BindingUI();
        RestoreUI();
    }

    public async Task SaveAsync()
    {
        if (!Enum.TryParse<ETheme>(CurrentTheme, out _) || CurrentFontSize < Global.MinFontSize
            || CurrentFontSize >= Global.MinFontSize + Global.MinFontSizeCount || string.IsNullOrWhiteSpace(CurrentLanguage))
            throw new InvalidOperationException(Common.WorkspaceText.T("请选择有效的主题、字号和语言。", "Choose a valid theme, font size and language."));
        var ui = _config.UiItem;
        var previous = (ui.CurrentTheme, ui.CurrentFontSize, ui.CurrentLanguage, ui.CurrentFontFamily);
        ui.CurrentTheme = CurrentTheme; ui.CurrentFontSize = CurrentFontSize;
        ui.CurrentLanguage = CurrentLanguage; ui.CurrentFontFamily = CurrentFontFamily;
        if (await ConfigHandler.SaveConfig(_config) != 0)
        {
            (ui.CurrentTheme, ui.CurrentFontSize, ui.CurrentLanguage, ui.CurrentFontFamily) = previous;
            throw new IOException(Common.WorkspaceText.T("外观设置保存失败，草稿已保留。", "Could not save appearance; the draft is retained."));
        }
        RestoreUI();
        if (previous.CurrentLanguage != CurrentLanguage) NoticeManager.Instance.Enqueue(ResUI.NeedRebootTips);
    }

    public void RevertPreview()
    {
        CurrentTheme = _config.UiItem.CurrentTheme ?? nameof(ETheme.FollowSystem);
        CurrentFontSize = _config.UiItem.CurrentFontSize >= Global.MinFontSize ? _config.UiItem.CurrentFontSize : 14;
        CurrentLanguage = _config.UiItem.CurrentLanguage;
        CurrentFontFamily = _config.UiItem.CurrentFontFamily ?? string.Empty;
        RestoreUI();
    }

    private void RestoreUI()
    {
        ModifyTheme();
        ModifyFontFamily();
        ModifyFontSize();
    }

    private void BindingUI()
    {
        CurrentTheme = Enum.TryParse<ETheme>(_config.UiItem.CurrentTheme, out var theme) ? theme.ToString() : nameof(ETheme.FollowSystem);
        CurrentFontSize = _config.UiItem.CurrentFontSize >= Global.MinFontSize ? _config.UiItem.CurrentFontSize : 14;
        CurrentLanguage = _config.UiItem.CurrentLanguage;
        CurrentFontFamily = _config.UiItem.CurrentFontFamily ?? string.Empty;

        this.WhenAnyValue(x => x.CurrentTheme)
            .Skip(1)
            .Where(y => y.IsNotEmpty())
            .SubscribeAsync(async _ =>
            {
                if (IsDraft) { ModifyTheme(); return; }
                if (_config.UiItem.CurrentTheme != CurrentTheme)
                {
                    _config.UiItem.CurrentTheme = CurrentTheme;
                    ModifyTheme();
                    await ConfigHandler.SaveConfig(_config);
                }
            });

        this.WhenAnyValue(x => x.CurrentFontSize)
            .Skip(1)
            .Where(y => y > 0)
            .SubscribeAsync(async _ =>
            {
                if (IsDraft) { ModifyFontSize(); return; }
                if (_config.UiItem.CurrentFontSize != CurrentFontSize && CurrentFontSize >= Global.MinFontSize)
                {
                    _config.UiItem.CurrentFontSize = CurrentFontSize;
                    ModifyFontSize();
                    await ConfigHandler.SaveConfig(_config);
                }
            });

        this.WhenAnyValue(x => x.CurrentLanguage)
            .Skip(1)
            .Where(y => !y.IsNullOrEmpty())
            .SubscribeAsync(async _ =>
            {
                if (IsDraft) return;
                if (CurrentLanguage.IsNotEmpty() && _config.UiItem.CurrentLanguage != CurrentLanguage)
                {
                    _config.UiItem.CurrentLanguage = CurrentLanguage;
                    Thread.CurrentThread.CurrentUICulture = new(CurrentLanguage);
                    await ConfigHandler.SaveConfig(_config);
                    NoticeManager.Instance.Enqueue(ResUI.NeedRebootTips);
                }
            });

        this.WhenAnyValue(x => x.CurrentFontFamily).Skip(1).Subscribe(_ => ModifyFontFamily());
    }

    private void ModifyTheme()
    {
        var app = Application.Current;
        if (app is not null)
        {
            app.RequestedThemeVariant = CurrentTheme switch
            {
                nameof(ETheme.Dark) => ThemeVariant.Dark,
                nameof(ETheme.Light) => ThemeVariant.Light,
                nameof(ETheme.Aquatic) => SemiTheme.Aquatic,
                nameof(ETheme.Desert) => SemiTheme.Desert,
                nameof(ETheme.Dusk) => SemiTheme.Dusk,
                nameof(ETheme.NightSky) => SemiTheme.NightSky,
                _ => ThemeVariant.Default,
            };
        }
    }

    private void ModifyFontSize()
    {
        double size = CurrentFontSize;
        if (size < Global.MinFontSize)
        {
            return;
        }

        if (Application.Current is not { } app) return;
        app.Resources["WorkspaceFontSize"] = size;
        app.Resources["WorkspaceTitleSize"] = size * 24 / 14;
        app.Resources["WorkspaceSectionSize"] = size * 16 / 14;
        app.Resources["WorkspaceCaptionSize"] = size * 12 / 14;
        app.Resources["WorkspaceMetricSize"] = size * 22 / 14;
        app.Resources["WorkspaceRowHeight"] = Math.Max(53, size * 3.8);
    }

    private void ModifyFontFamily()
    {
        if (_fontStyle != null) Application.Current?.Styles.Remove(_fontStyle);
        _fontStyle = null;
        var currentFontFamily = CurrentFontFamily;
        if (currentFontFamily.IsNullOrEmpty())
        {
            return;
        }

        try
        {
            Style style = new(x => Selectors.Or(
                x.OfType<Button>(),
                x.OfType<TextBox>(),
                x.OfType<TextBlock>(),
                x.OfType<SelectableTextBlock>(),
                x.OfType<Menu>(),
                x.OfType<ContextMenu>(),
                x.OfType<DataGridRow>(),
                x.OfType<ListBoxItem>(),
                x.OfType<HeaderedContentControl>(),
                x.OfType<WindowNotificationManager>(),
                x.OfType<TextEditor>()
            ));
            style.Add(new Setter()
            {
                Property = TemplatedControl.FontFamilyProperty,
                Value = new FontFamily(currentFontFamily),
            });
            _fontStyle = style;
            Application.Current?.Styles.Add(style);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ModifyFontFamily", ex);
        }
    }
}
