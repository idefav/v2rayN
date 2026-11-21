namespace ServiceLib.Models.Dto;

public sealed class RoutePickerChoice(bool initial, Action<bool> changed) : ReactiveObject
{
    private bool _selected = initial;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (value == _selected) return;
            this.RaiseAndSetIfChanged(ref _selected, value);
            changed(value);
        }
    }
}
