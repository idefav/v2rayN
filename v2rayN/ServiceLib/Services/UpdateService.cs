namespace ServiceLib.Services;

public partial class UpdateService(Config config, Func<bool, string, Task> updateFunc)
{
    private readonly Config _config = JsonUtils.Deserialize<Config>(JsonSerializer.Serialize(config))!;
    private readonly Func<bool, string, Task>? _updateFunc = updateFunc;
    private static readonly string _tag = "UpdateService";
    public async Task<UpdateResult> CheckHasUpdateOnly(ECoreType type, bool preRelease, bool blProxy = true, CancellationToken cancellationToken = default)
    {
        if (!CoreInfoManager.Instance.IsCheckUpdateSupported(type))
        {
            return new UpdateResult(false, ResUI.MsgNotSupport);
        }

        var downloadHandle = new DownloadService();
        var checkPreRelease = CoreInfoManager.Instance.GetCheckPreRelease(type, preRelease);
        return await CheckUpdateAsync(downloadHandle, type, checkPreRelease, blProxy, cancellationToken);
    }

    public async Task<List<string>> CheckHasUpdateOnlyAll(bool blProxy = true, CancellationToken cancellationToken = default)
    {
        var msgs = new List<string>();
        foreach (var type in CoreInfoManager.Instance.GetCheckUpdateCoreTypes())
        {
            if (!(_config.CheckUpdateItem.SelectedCoreTypes?.Contains(type.ToString()) ?? true))
            {
                continue;
            }

            var preRelease = _config.CheckUpdateItem.CheckPreReleaseCoreTypes?.Contains(type.ToString()) ?? false;
            var result = await CheckHasUpdateOnly(type, preRelease, blProxy, cancellationToken);
            // A settings save may have cleared this result while the old repository was still being queried.
            if (!string.Equals(_config.CheckUpdateItem.RepositoryOverrides?.GetValueOrDefault(type.ToString()),
                AppManager.Instance.Config.CheckUpdateItem.RepositoryOverrides?.GetValueOrDefault(type.ToString()), StringComparison.Ordinal)) continue;
            if (result.Success && result.Version != null)
            {
                var msg = result.Msg ?? string.Format(ResUI.MsgCheckUpdateHasNewVersion, type, result.Version);
                msgs.Add(msg);
                AppManager.Instance.SetLastCheckUpdateResult(type, msg);
            }
            else
            {
                AppManager.Instance.SetLastCheckUpdateResult(type, result.Msg);
            }
        }
        return msgs;
    }

    public async Task UpdateGeoFileAll(bool blProxy = true, CancellationToken cancellationToken = default)
    {
        var requests = new List<FileDownloadRequest>();
        requests.AddRange(GetGeoFilesRequest());
        requests.AddRange(GetOtherFilesRequest());
        requests.AddRange(await GetSrsFileAllRequest());
        // NOTE: srs files are more small, so we reverse the order to ensure a good download experience for the user.
        requests.Reverse();
        await DownloadGeoFiles(requests, blProxy, cancellationToken);
        await UpdateFunc(true, string.Format(ResUI.MsgDownloadGeoFileSuccessfully, "geo"));
    }

    #region CheckUpdate private

    private async Task<UpdateResult> CheckUpdateAsync(DownloadService downloadHandle, ECoreType type, bool preRelease, bool blProxy, CancellationToken cancellationToken = default)
    {
        try
        {
            return await CheckRepositoryAsync(downloadHandle, type, preRelease, blProxy, cancellationToken);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(false, ex.Message);
            return new UpdateResult(false, ex.Message);
        }
    }
    [GeneratedRegex(@"v?(?<version>\d+\.\d+\.\d+(?:-[0-9a-zA-Z.-]+)?(?:\+[0-9a-zA-Z.-]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex SemVerRegex();

    public async Task<SemanticVersion> GetCoreVersion(ECoreType type, CancellationToken cancellationToken = default)
    {
        try
        {
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(type);
            var filePath = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out _);

            if (!File.Exists(filePath))
            {
                return new SemanticVersion("");
            }

            var result = await Utils.GetCliWrapOutput(filePath, coreInfo.VersionArg, cancellationToken);
            var echo = result ?? "";
            return ParseCoreVersion(echo) ?? new SemanticVersion("");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(false, ex.Message);
            return new SemanticVersion("");
        }
    }
    #endregion CheckUpdate private

    #region Geo private

    private List<FileDownloadRequest> GetGeoFilesRequest()
    {
        UpdateSources.ValidateTemplate(_config.ConstItem.GeoSourceUrl, 1);
        var geoUrl = string.IsNullOrEmpty(_config?.ConstItem.GeoSourceUrl)
            ? Global.GeoUrl
            : _config.ConstItem.GeoSourceUrl;

        List<string> files = ["geosite", "geoip"];
        return
        [
            .. from geoName in files
            let fileName = $"{geoName}.dat"
            let targetPath = Utils.GetBinPath($"{fileName}")
            let url = string.Format(geoUrl, geoName)
            select new FileDownloadRequest()
            {
                FileUrl = url,
                FilePath = targetPath,
                DisplayFileName = fileName,
            },
        ];
    }

    private List<FileDownloadRequest> GetOtherFilesRequest()
    {
        //If it is not in China area, no update is required
        if (_config.ConstItem.GeoSourceUrl.IsNotEmpty())
        {
            return [];
        }

        return
        [
            .. Global.OtherGeoUrls.Select(url =>
            {
                var fileName = Path.GetFileName(url);
                var targetPath = Utils.GetBinPath($"{fileName}");
                return new FileDownloadRequest()
                {
                    FileUrl = url,
                    FilePath = targetPath,
                    DisplayFileName = fileName,
                };
            }),
        ];
    }

    private async Task<List<FileDownloadRequest>> GetSrsFileAllRequest()
    {
        var geoipFiles = new List<string>();
        var geoSiteFiles = new List<string>();

        // Collect from routing rules
        var routingItems = await AppManager.Instance.RoutingItems();
        foreach (var routing in routingItems)
        {
            var rules = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet);
            foreach (var item in rules ?? [])
            {
                AddPrefixedItems(item.Ip, Global.GeoIPPrefix, geoipFiles);
                AddPrefixedItems(item.Domain, Global.GeoSitePrefix, geoSiteFiles);
            }
        }

        // Collect from DNS configuration
        var dnsItem = await AppManager.Instance.GetDNSItem(ECoreType.sing_box);
        if (dnsItem != null)
        {
            ExtractDnsRuleSets(dnsItem.NormalDNS, geoipFiles, geoSiteFiles);
            ExtractDnsRuleSets(dnsItem.TunDNS, geoipFiles, geoSiteFiles);
        }

        // Append default items
        geoSiteFiles.AddRange(["google", "cn", "geolocation-cn", "category-ads-all"]);

        // Download files
        var path = Utils.GetBinPath("srss");
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        return
        [
            .. geoipFiles.Distinct().Select(f => (type: "geoip", file: f))
                .Concat(geoSiteFiles.Distinct().Select(f => (type: "geosite", file: f)))
                .Select(item => GetSrsFileRequest(item.type, item.file)),
        ];
    }

    private void AddPrefixedItems(List<string>? items, string prefix, List<string> output)
    {
        if (items == null)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item.StartsWith(prefix))
            {
                output.Add(item.Substring(prefix.Length));
            }
        }
    }

    private void ExtractDnsRuleSets(string? dnsJson, List<string> geoipFiles, List<string> geoSiteFiles)
    {
        if (string.IsNullOrEmpty(dnsJson))
        {
            return;
        }

        try
        {
            var dns = JsonUtils.Deserialize<Dns4Sbox>(dnsJson);
            if (dns?.rules != null)
            {
                foreach (var rule in dns.rules)
                {
                    ExtractSrsRuleSets(rule, geoipFiles, geoSiteFiles);
                }
            }
        }
        catch { }
    }

    private void ExtractSrsRuleSets(Rule4Sbox? rule, List<string> geoipFiles, List<string> geoSiteFiles)
    {
        if (rule == null)
        {
            return;
        }

        AddPrefixedItems(rule.rule_set, "geosite-", geoSiteFiles);
        AddPrefixedItems(rule.rule_set, "geoip-", geoipFiles);

        // Handle nested rules recursively
        if (rule.rules != null)
        {
            foreach (var nestedRule in rule.rules)
            {
                ExtractSrsRuleSets(nestedRule, geoipFiles, geoSiteFiles);
            }
        }
    }

    private FileDownloadRequest GetSrsFileRequest(string type, string srsName)
    {
        UpdateSources.ValidateTemplate(_config.ConstItem.SrsSourceUrl, 2);
        var srsUrl = string.IsNullOrEmpty(_config.ConstItem.SrsSourceUrl)
                        ? Global.SingboxRulesetUrl
                        : _config.ConstItem.SrsSourceUrl;

        var fileName = $"{type}-{srsName}.srs";
        var targetPath = Path.Combine(Utils.GetBinPath("srss"), fileName);
        var url = string.Format(srsUrl, type, $"{type}-{srsName}", srsName);

        return new FileDownloadRequest()
        {
            FileUrl = url,
            FilePath = targetPath,
            DisplayFileName = fileName,
        };
    }

    private async Task DownloadGeoFiles(List<FileDownloadRequest> requests, bool blProxy, CancellationToken cancellationToken = default)
    {
        var tmpFilePathDict = new Dictionary<string, string>();
        var tmpFileRequests = new List<FileDownloadRequest>();
        foreach (var request in requests)
        {
            var tmpFilePath = Utils.GetTempPath(Utils.GetGuid());
            tmpFilePathDict[request.FilePath] = tmpFilePath;
            tmpFileRequests.Add(request with
            {
                FilePath = tmpFilePath,
            });
        }

        if (tmpFileRequests.Count == 0) return;
        var downloadHandle = new DownloadService();
        var completed = false;
        Exception? error = null;
        downloadHandle.UpdateCompleted += (_, state) =>
        {
            if (state.Success) completed = true;
            else _ = UpdateFunc(false, state.Msg);
        };
        downloadHandle.Error += (_, state) => error = state.GetException();
        try
        {
            await downloadHandle.DownloadSmallFilesAsync(tmpFileRequests, blProxy, cancellationToken);
            if (error != null || !completed || tmpFileRequests.Any(r => !File.Exists(r.FilePath)))
                throw error ?? new IOException("Rule downloads did not complete");
            foreach (var request in requests)
                File.Move(tmpFilePathDict[request.FilePath], request.FilePath, true);
        }
        finally
        {
            foreach (var temporary in tmpFilePathDict.Values) File.Delete(temporary);
        }
    }

    #endregion Geo private

    private async Task UpdateFunc(bool notify, string msg)
    {
        await _updateFunc?.Invoke(notify, msg);
    }
}
