namespace ServiceLib.Services;

public partial class UpdateService
{
    internal static SemanticVersion? ParseCoreVersion(string output)
    {
        var value = SemVerRegex().Match(output).Groups["version"].Value;
        return UpdateSources.TryVersion(value, out var version) ? version : null;
    }

    private async Task<UpdateResult> CheckRepositoryAsync(DownloadService download, ECoreType type, bool prerelease,
        bool proxy, CancellationToken token)
    {
        if (!CoreInfoManager.Instance.IsCheckUpdateSupported(type)) return new(false, ResUI.MsgNotSupport);
        var repository = UpdateSources.Repository(_config!, type);
        var info = CoreInfoManager.Instance.GetCoreInfo(type)!;
        // Snapshot this repository for both release lookup and download; never read mutable settings again.
        var json = await download.TryDownloadString($"{Global.GithubApiUrl}/{repository}/releases?per_page=100", proxy, Global.AppName, token);
        if (string.IsNullOrWhiteSpace(json)) throw new IOException($"{repository}: {ResUI.OperationFailed}");
        var release = UpdateSources.SelectRelease(JsonUtils.Deserialize<List<GitHubRelease>>(json) ?? [], prerelease, info.LockedMaxVersion);
        var version = new SemanticVersion(release.TagName);
        var current = type == ECoreType.v2rayN ? new SemanticVersion(AppUpdateProvenance.CurrentVersion()) : await GetCoreVersion(type, token);
        var switching = UpdateSources.IsSourceSwitch(InstalledUpdateStore.Repository(type), repository);
        var message = switching ? $"{UpdateSourceText.Switch}: {repository} → {release.TagName}"
            : string.Format(ResUI.MsgCheckUpdateHasNewVersion, type, release.TagName);
        if (!switching && current >= version)
            return new(false, string.Format(type == ECoreType.v2rayN ? ResUI.IsLatestN : ResUI.IsLatestCore, type, current));
        var platform = Utils.IsWindows() ? "windows" : Utils.IsMacOS() ? "macos" : "linux";
        var name = UpdateSources.AssetName(info, release.TagName!, platform, RuntimeInformation.ProcessArchitecture,
            File.Exists(Path.Combine(Utils.GetBaseDirectory(), "libHarfBuzzSharp.dll")));
        var asset = release.Assets?.SingleOrDefault(a => a.Name == name);
        if (asset?.BrowserDownloadUrl == null) throw new InvalidDataException($"{repository} {release.TagName}: missing asset {name}");
        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidDataException("Invalid release asset URL");
        return new(true, version)
        {
            Repository = repository, Tag = release.TagName, Url = asset.BrowserDownloadUrl,
            IsSourceSwitch = switching, Msg = message
        };
    }

    public async Task<UpdateResult> DownloadUpdateAsync(ECoreType type, bool prerelease, bool proxy, CancellationToken token = default)
    {
        var download = new DownloadService();
        var result = await CheckUpdateAsync(download, type, prerelease, proxy, token);
        if (!result.Success) return result;
        await UpdateFunc(false, result.Msg!);
        var extension = result.Url!.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz" : Path.GetExtension(new Uri(result.Url).AbsolutePath);
        var file = Utils.GetTempPath(Guid.NewGuid().ToString("N") + extension);
        var complete = false;
        Exception? error = null;
        download.UpdateCompleted += (_, state) => { if (state.Success) complete = true; };
        download.Error += (_, state) => error = state.GetException();
        try
        {
            await download.DownloadFileAsync(new() { FileUrl = result.Url, FilePath = file }, proxy, token);
            if (error != null || !complete || !File.Exists(file)) throw error ?? new IOException("Download did not complete");
            result.FileName = file;
            return result;
        }
        catch
        {
            File.Delete(file);
            throw;
        }
    }
}
