namespace ServiceLib.Manager;

public class ProfileExManager
{
    private static readonly Lazy<ProfileExManager> _instance = new(() => new());
    private ConcurrentBag<ProfileExItem> _lstProfileEx = [];
    private readonly ConcurrentQueue<string> _queIndexIds = new();
    private readonly ConcurrentDictionary<string, byte> _queuedIds = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _itemsGate = new();
    public static ProfileExManager Instance => _instance.Value;
    private static readonly string _tag = "ProfileExHandler";

    public ProfileExManager()
    {
        //Init();
    }

    public async Task Init()
    {
        await InitData();
    }

    public async Task<ConcurrentBag<ProfileExItem>> GetProfileExs()
    {
        return await Task.FromResult(_lstProfileEx);
    }

    private async Task InitData()
    {
        await SQLiteHelper.Instance.ExecuteAsync($"delete from ProfileExItem where indexId not in ( select indexId from ProfileItem )");

        _lstProfileEx = new(await SQLiteHelper.Instance.TableAsync<ProfileExItem>().ToListAsync());
    }

    private void IndexIdEnqueue(string indexId)
    {
        if (indexId.IsNotEmpty() && _queuedIds.TryAdd(indexId, 0))
        {
            _queIndexIds.Enqueue(indexId);
        }
    }

    private async Task SaveQueueIndexIds()
    {
        var cnt = _queIndexIds.Count;
        if (cnt > 0)
        {
            var lstExists = await SQLiteHelper.Instance.TableAsync<ProfileExItem>().ToListAsync();
            List<ProfileExItem> lstInserts = [];
            List<ProfileExItem> lstUpdates = [];

            for (var i = 0; i < cnt; i++)
            {
                if (!_queIndexIds.TryDequeue(out var id)) break;
                _queuedIds.TryRemove(id, out _);
                var item = lstExists.FirstOrDefault(t => t.IndexId == id);
                var itemNew = _lstProfileEx?.FirstOrDefault(t => t.IndexId == id);
                if (itemNew is null)
                {
                    continue;
                }

                if (item is not null)
                {
                    lstUpdates.Add(itemNew);
                }
                else
                {
                    lstInserts.Add(itemNew);
                }
            }

            try
            {
                if (lstInserts.Count > 0)
                {
                    await SQLiteHelper.Instance.InsertAllAsync(lstInserts);
                }

                if (lstUpdates.Count > 0)
                {
                    await SQLiteHelper.Instance.UpdateAllAsync(lstUpdates);
                }
            }
            catch (Exception ex)
            {
                foreach (var item in lstInserts.Concat(lstUpdates)) IndexIdEnqueue(item.IndexId);
                Logging.SaveLog(_tag, ex);
            }
        }
    }

    private ProfileExItem AddProfileEx(string indexId)
    {
        var profileEx = new ProfileExItem()
        {
            IndexId = indexId,
            Delay = 0,
            Speed = 0,
            Sort = 0,
            Message = string.Empty
        };
        _lstProfileEx.Add(profileEx);
        IndexIdEnqueue(indexId);
        return profileEx;
    }

    private ProfileExItem GetProfileExItem(string? indexId)
    {
        lock (_itemsGate) return _lstProfileEx.FirstOrDefault(t => t.IndexId == indexId) ?? AddProfileEx(indexId);
    }

    public async Task ClearAll()
    {
        await SQLiteHelper.Instance.ExecuteAsync($"delete from ProfileExItem ");
        _lstProfileEx = [];
    }

    public async Task SaveTo()
    {
        await _saveGate.WaitAsync();
        try
        {
            await SaveQueueIndexIds();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        finally { _saveGate.Release(); }
    }

    public void SetTestDelay(string indexId, int delay)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.Delay = delay;
        IndexIdEnqueue(indexId);
    }

    public void SetProbeResult(ServerProbeResult result)
    {
        var item = GetProfileExItem(result.NodeId);
        item.ProbeIdentity = result.Identity;
        item.TcpProbe = JsonSerializer.Serialize(result.Tcp);
        item.ProxyProbe = JsonSerializer.Serialize(result.Proxy);
        item.ProbeCheckedAt = result.CheckedAt.ToUnixTimeSeconds();
        if (result.Proxy.State is ServerProbeState.Passed or ServerProbeState.Responded)
            item.Delay = (int)Math.Max(1, result.Proxy.Milliseconds);
        else if (result.Proxy.State is ServerProbeState.Failed or ServerProbeState.Timeout) item.Delay = -1;
        IndexIdEnqueue(result.NodeId);
    }

    public void SetTcpDelay(string indexId, int delay)
    {
        var item = GetProfileExItem(indexId);
        item.TcpProbe = JsonSerializer.Serialize(new ServerProbeStage(
            delay >= 0 ? ServerProbeState.Passed : ServerProbeState.Failed, Math.Max(0, delay)));
        IndexIdEnqueue(indexId);
    }

    public void SetTestSpeed(string indexId, decimal speed)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.Speed = speed;
        IndexIdEnqueue(indexId);
    }

    public void SetTestMessage(string indexId, string message)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.Message = message;
        IndexIdEnqueue(indexId);
    }

    public void SetTestIpInfo(string indexId, string ipInfo)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.IpInfo = ipInfo;
        IndexIdEnqueue(indexId);
    }

    public void SetSort(string indexId, int sort)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.Sort = sort;
        IndexIdEnqueue(indexId);
    }

    public int GetSort(string indexId)
    {
        var profileEx = _lstProfileEx.FirstOrDefault(t => t.IndexId == indexId);
        if (profileEx == null)
        {
            return 0;
        }
        return profileEx.Sort;
    }

    public int GetMaxSort()
    {
        if (_lstProfileEx.Count <= 0)
        {
            return 0;
        }
        return _lstProfileEx.Max(t => t?.Sort ?? 0);
    }
}
