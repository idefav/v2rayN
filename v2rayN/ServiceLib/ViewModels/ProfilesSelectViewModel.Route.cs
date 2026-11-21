using static ServiceLib.Services.RoutingCoreCompatibility;

namespace ServiceLib.ViewModels;

public partial class ProfilesSelectViewModel
{
    public ECoreType? RouteCore { get; }
    public bool IsRoutePicker => RouteCore.HasValue;
    public Task Initialization { get; }
    public string ManualLabel => T("手动选择机器", "Select machines");
    public string FollowLabel => T("跟随一个订阅", "Follow a subscription");
    public string ClearLabel => T("清除选择", "Clear selection");
    [Reactive] public partial bool FollowSubscription { get; set; }
    [Reactive] public partial string RouteStatus { get; set; }
    [Reactive] public partial bool RouteCanConfirm { get; set; }
    [Reactive] public partial bool AllVisibleChecked { get; set; }

    private readonly RouteOutboundTarget? _originalTarget;
    private readonly string? _legacyName;
    private readonly HashSet<string> _chosen;
    private readonly HashSet<string> _valid = [];
    private List<ProfileItemModel> _routeRows = [];
    private bool _routeReady;
    private bool _changingMany;

    public RouteOutboundTarget RouteSelection() => FollowSubscription
        ? new() { SubscriptionId = SelectedSub?.Id, SubscriptionName = SelectedSub?.Remarks }
        : new() { ProfileIds = _chosen.Order(StringComparer.Ordinal).ToList() };

    private async Task InitRouteAsync()
    {
        try { await LoadRouteCatalogAsync(); }
        catch (Exception ex)
        {
            Logging.SaveLog("Route profile picker", ex);
            RouteStatus = T("读取节点失败，请重新打开选择器", "Failed to load machines. Reopen the picker.");
        }
    }

    private async Task LoadRouteCatalogAsync()
    {
        await RefreshSubscriptions();
        var profiles = (await AppManager.Instance.ProfileItems("") ?? []).Where(RouteTargetPolicy.CanSelect).ToDictionary(p => p.IndexId);
        _valid.UnionWith(profiles.Values.Where(p => NodeValidator.Validate(p, RouteCore!.Value).Success).Select(p => p.IndexId));
        _routeRows = (await GetProfileItemsEx("", "") ?? []).Where(row => profiles.ContainsKey(row.IndexId)).ToList();
        if (_originalTarget == null && !string.IsNullOrEmpty(_legacyName) && !Global.OutboundTags.Contains(_legacyName))
        {
            var matches = _routeRows.Where(row => row.Remarks == _legacyName).Take(2).ToArray();
            if (matches.Length == 1) _chosen.Add(matches[0].IndexId);
        }
        foreach (var row in _routeRows)
            row.RouteChoice = new(_chosen.Contains(row.IndexId), value =>
            {
                if (value) _chosen.Add(row.IndexId);
                else _chosen.Remove(row.IndexId);
                if (!_changingMany) UpdateRouteStatus();
            });
        _routeReady = true;
        ApplyRouteFilter();
    }

    private void ApplyRouteFilter()
    {
        if (!_routeReady) return;
        var filter = ServerFilter?.Trim() ?? "";
        ProfileItems.ReplaceRange(_routeRows.Where(row =>
            (string.IsNullOrEmpty(SelectedSub?.Id) || row.Subid == SelectedSub.Id)
            && (filter.Length == 0 || row.Remarks.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.Address.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList());
        UpdateRouteStatus();
    }

    public void SelectVisible(bool selected) => SetRouteChoices(ProfileItems, selected);

    public void ClearRouteChoices()
    {
        _chosen.Clear();
        SetRouteChoices(_routeRows, false);
    }

    private void SetRouteChoices(IEnumerable<ProfileItemModel> rows, bool value)
    {
        _changingMany = true;
        try { foreach (var row in rows) row.RouteChoice!.Selected = value; }
        finally { _changingMany = false; }
        UpdateRouteStatus();
    }

    private void UpdateRouteStatus()
    {
        if (!_routeReady) return;
        RouteCanConfirm = CanOk();
        AllVisibleChecked = ProfileItems.Count > 0 && ProfileItems.All(r => _chosen.Contains(r.IndexId));
        if (FollowSubscription && string.IsNullOrEmpty(SelectedSub?.Id))
        {
            RouteStatus = _originalTarget?.SubscriptionId is { } id && SubItems.All(s => s.Id != id)
                ? T("原订阅已删除，请选择具体订阅", "Saved subscription missing. Select a subscription.")
                : T("请选择具体订阅", "Select a subscription.");
            return;
        }
        if (!FollowSubscription && _chosen.Count == 0)
        {
            RouteStatus = T("请勾选至少一台机器", "Select at least one machine.");
            return;
        }
        var target = RouteSelection();
        var rows = _routeRows.Where(row => FollowSubscription ? row.Subid == SelectedSub.Id : _chosen.Contains(row.IndexId)).ToList();
        RouteStatus = RouteTargetPolicy.Summary(target) + " · " +
            RouteTargetPolicy.Availability(target, true, rows.Count, rows.Count(r => _valid.Contains(r.IndexId)), RouteCore!.Value);
    }
}
