using System.ComponentModel;

namespace v2rayN.Desktop.Base;

/// <summary>A settings/management body that can live in the workspace or a native window.</summary>
public interface IWorkspacePage
{
    event Action<object?>? CloseRequested;
    string PageTitle { get; }
    object? Model { get; }
    void Close(object? result = null);
}

public class PageBase<TViewModel> : ReactiveUserControl<TViewModel>, IWorkspacePage where TViewModel : class
{
    private bool _presented;
    public PageBase()
    {
        Loaded += (_, _) =>
        {
            if (_presented) return;
            _presented = true;
            Common.FormPresentation.Apply(this);
        };
    }
    public event Action<object?>? CloseRequested;
    public event EventHandler<CancelEventArgs>? Closing;
    public string PageTitle => Tag?.ToString() ?? Global.AppName;
    public object? Model => ViewModel;

    public void Close(object? result = null)
    {
        var args = new CancelEventArgs();
        Closing?.Invoke(this, args);
        if (!args.Cancel)
        {
            CloseRequested?.Invoke(result);
        }
    }
}
