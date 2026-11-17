namespace ServiceLib.ViewModels;

public sealed record ServerPickerGroup(string Id, string Name)
{
    public override string ToString() => Name;
}
public sealed partial class ServerPickerRow : ReactiveObject
{
    public required ProfileItem Node { get; init; }
    public string GroupName { get; init; } = "";
    public string GroupId { get; init; } = "";
    public bool IsHeader { get; init; }
    public string Name => string.IsNullOrWhiteSpace(Node.Remarks) ? Node.Address : Node.Remarks;
    public string Endpoint => Node.Address.Contains(':') ? $"[{Node.Address}]:{Node.Port}" : $"{Node.Address}:{Node.Port}";
    public string Identity => $"{Node.ConfigType} · {GroupName}";
    public string GroupTitle { get; init; } = "";
    [Reactive] public partial string Results { get; set; } = "";
    [Reactive] public partial bool Testing { get; set; }
    [Reactive] public partial bool Favorite { get; set; }
    public string Selection { get; init; } = "";
}

/// <summary>Both desktop surfaces use the same grouping, immutable batch and stale-result guards.</summary>
public sealed partial class ServerPickerViewModel : MyReactiveObject, IDisposable
{
    private readonly Config _settings;
    private readonly IServerProbeService _probe;
    private readonly Func<string, Task<ProfileItem?>> _lookup;
    private List<ServerPickerRow> _all = [];
    private CancellationTokenSource? _run;
    private Guid _generation;
    private bool _open;
    public event Action? RowsChanged;
    public IReadOnlyList<ServerPickerRow> Rows { get; private set; } = [];
    public IReadOnlyList<ServerPickerGroup> Groups { get; private set; } = [];
    public string GroupId { get; set; } = "*";
    public string Search { get; set; } = "";
    public bool FavoritesOnly { get; set; }
    public int VisibleCount => Rows.Count(r => !r.IsHeader);
    [Reactive] public partial bool Busy { get; set; }
    [Reactive] public partial string Progress { get; set; } = "";
    public ServerPickerViewModel(Config settings, IServerProbeService? probe = null,
        Func<string, Task<ProfileItem?>>? lookup = null)
    {
        _settings = settings; _probe = probe ?? new ServerProbeService();
        _lookup = lookup ?? (id => AppManager.Instance.GetProfileItem(id));
    }
    public async Task OpenAsync()
    {
        Cancel(); _open = true;
        var generation = _generation;
        var nodes = await AppManager.Instance.ProfileItems("") ?? [];
        var groups = await AppManager.Instance.SubItems() ?? [];
        var measurements = (await ProfileExManager.Instance.GetProfileExs()).ToDictionary(x => x.IndexId);
        await UiThread.RunAsync(() => Load(nodes, groups, measurements), () => _open && generation == _generation);
    }
    internal void Load(IEnumerable<ProfileItem> nodes, IEnumerable<SubItem> groups, IReadOnlyDictionary<string, ProfileExItem> measurements)
    {
        _open = true;
        var orderedGroups = groups.OrderBy(x => x.Sort).ToArray();
        Groups = new[] { new ServerPickerGroup("*", ProxyCheckText.T("全部分组", "All groups")),
            new ServerPickerGroup("", ProxyCheckText.T("未分组", "Ungrouped")) }
            .Concat(orderedGroups.Select(x => new ServerPickerGroup(x.Id, x.Remarks))).ToArray();
        var names = orderedGroups.ToDictionary(g => g.Id, g => g.Remarks);
        _all = nodes.OrderBy(n => Array.FindIndex(orderedGroups, g => g.Id == n.Subid))
            .ThenBy(n => measurements.GetValueOrDefault(n.IndexId)?.Sort ?? 0).Select(n =>
            {
                var copy = JsonUtils.DeepCopy(n);
                var groupId = names.ContainsKey(copy.Subid ?? "") ? copy.Subid : "";
                var measurement = measurements.GetValueOrDefault(n.IndexId);
                var summary = ProxyCheckText.T("未测试", "Untested");
                if (measurement?.ProbeIdentity == ServerProbeService.Identity(n))
                {
                    try { summary = Format(JsonSerializer.Deserialize<ServerProbeStage>(measurement.TcpProbe ?? "null"),
                        JsonSerializer.Deserialize<ServerProbeStage>(measurement.ProxyProbe ?? "null"))
                        + " · " + DateTimeOffset.FromUnixTimeSeconds(measurement.ProbeCheckedAt).ToLocalTime().ToString("HH:mm"); }
                    catch (JsonException) { }
                }
                else if (measurement?.Delay != 0 && measurement != null)
                    summary = ProxyCheckText.T("历史结果：", "Historical: ") + (measurement.Delay > 0 ? $"{measurement.Delay} ms" : ProxyCheckText.T("失败", "Failed"));
                return new ServerPickerRow { Node = copy, GroupId = groupId, GroupName = names.GetValueOrDefault(groupId, Groups[1].Name),
                    Favorite = _settings.UiItem.ConnectionFavorites.Contains(n.IndexId), Results = summary,
                    Selection = string.Join(" · ", new[] {
                        CoreManager.Instance.RuntimeSummary?.ProfileId == n.IndexId ? ProxyCheckText.T("正在使用", "In use") : null,
                        _settings.IndexId == n.IndexId ? ProxyCheckText.T("已选中", "Selected") : null }.Where(s => s != null)) };
            }).ToList();
        GroupId = _all.FirstOrDefault(x => x.Node.IndexId == _settings.IndexId)?.GroupId ?? "*";
        Search = ""; FavoritesOnly = false; Refresh();
    }
    public void Refresh()
    {
        var query = Search.Trim();
        var visible = _all.Where(r => (GroupId == "*" || r.GroupId == GroupId)
            && (!FavoritesOnly || r.Favorite)
            && (r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.Endpoint.Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.GroupName.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        Rows = visible.GroupBy(r => r.GroupId).SelectMany(group =>
            (GroupId == "*" ? new[] { new ServerPickerRow { Node = new(), IsHeader = true,
                GroupTitle = $"{group.First().GroupName} ({group.Count()})" } } : [])
                .Concat(group)).ToArray();
        RowsChanged?.Invoke();
    }
    public Task ToggleFavoriteAsync(ServerPickerRow row) => UiThread.RunTaskAsync(() => ToggleFavoriteCoreAsync(row));
    private async Task ToggleFavoriteCoreAsync(ServerPickerRow row)
    {
        var previous = _settings.UiItem.ConnectionFavorites.ToList();
        if (!_settings.UiItem.ConnectionFavorites.Remove(row.Node.IndexId)) _settings.UiItem.ConnectionFavorites.Add(row.Node.IndexId);
        if (await ConfigHandler.SaveConfig(_settings) != 0) _settings.UiItem.ConnectionFavorites = previous;
        row.Favorite = _settings.UiItem.ConnectionFavorites.Contains(row.Node.IndexId);
        Refresh();
    }
    public Task TestVisibleAsync() => UiThread.RunTaskAsync(() => TestAsync(Rows.Where(r => !r.IsHeader).ToArray()));
    public Task TestOneAsync(ServerPickerRow row) => UiThread.RunTaskAsync(() => TestAsync([row]));
    private async Task TestAsync(ServerPickerRow[] rows)
    {
        if (Busy || !_open || rows.Length == 0) return;
        Busy = true;
        var runId = _generation = Guid.NewGuid();
        using var run = new CancellationTokenSource();
        _run = run;
        var config = JsonUtils.DeepCopy(_settings);
        var finished = 0;
        foreach (var row in rows) { row.Testing = true; row.Results = ProxyCheckText.T("等待检测", "Queued"); }
        Progress = $"0 / {rows.Length}";
        try
        {
            await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = run.Token }, async (row, ct) =>
            {
                ServerProbeResult result;
                try { result = await _probe.TestAsync(row.Node, config, runId, ct); }
                catch (OperationCanceledException) { return; }
                catch { result = new(row.Node.IndexId, ServerProbeService.Identity(row.Node), runId, DateTimeOffset.UtcNow,
                    new(ServerProbeState.Failed), new(ServerProbeState.Failed)); }
                var current = await _lookup(result.NodeId);
                await UiThread.RunAsync(() =>
                {
                    if (result.RunId != runId || current == null || result.Identity != ServerProbeService.Identity(current)) return;
                    row.Results = Format(result.Tcp, result.Proxy) + " · " + result.CheckedAt.ToLocalTime().ToString("HH:mm");
                    row.Testing = false;
                    ProfileExManager.Instance.SetProbeResult(result);
                    Progress = $"{++finished} / {rows.Length}";
                }, () => _open && !ct.IsCancellationRequested && _generation == runId);
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            await UiThread.RunAsync(() =>
            {
                foreach (var row in rows.Where(r => r.Testing))
                { row.Testing = false; row.Results = ProxyCheckText.T("已取消", "Cancelled"); }
                if (_run == run) { _run = null; Busy = false; }
            });
            await ProfileExManager.Instance.SaveTo();
        }
    }
    private static string Format(ServerProbeStage? tcp, ServerProbeStage? proxy) =>
        $"TCP: {(tcp ?? new(ServerProbeState.Untested)).Display} · {ProxyCheckText.T("代理", "Proxy")}: {(proxy ?? new(ServerProbeState.Untested)).Display}";
    public void Cancel() { _generation = Guid.NewGuid(); _run?.Cancel(); }
    public void Close() { _open = false; Cancel(); }
    public void Dispose() => Close();
}
