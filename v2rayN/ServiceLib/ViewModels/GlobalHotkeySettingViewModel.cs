namespace ServiceLib.ViewModels;

public class GlobalHotkeySettingViewModel : MyReactiveObject, ICloseable
{
    public event EventHandler? RequestClose;

    private readonly List<KeyEventItem> _globalHotkeys;
    public IReadOnlyList<KeyEventItem> KeyEvents => _globalHotkeys;

    public ReactiveCommand<RxVoid, RxVoid> SaveCmd { get; }

    public GlobalHotkeySettingViewModel()
    {
        _config = AppManager.Instance.Config;

        _globalHotkeys = JsonUtils.DeepCopy(_config.GlobalHotkeys);
        foreach (var key in Enum.GetValues<EGlobalHotkey>()) GetKeyEventItem(key);

        SaveCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SaveSettingAsync();
        });
    }

    public KeyEventItem GetKeyEventItem(EGlobalHotkey eg)
    {
        var item = _globalHotkeys.FirstOrDefault((it) => it.EGlobalHotkey == eg);
        if (item != null)
        {
            return item;
        }

        item = new()
        {
            EGlobalHotkey = eg,
            Control = false,
            Alt = false,
            Shift = false,
            KeyCode = null
        };
        _globalHotkeys.Add(item);

        return item;
    }

    public void ResetKeyEventItem()
    {
        _globalHotkeys.Clear();
    }

    private async Task SaveSettingAsync()
    {
        _config.GlobalHotkeys = _globalHotkeys;

        if (await ConfigHandler.SaveConfig(_config) == 0)
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
        }
    }
}
