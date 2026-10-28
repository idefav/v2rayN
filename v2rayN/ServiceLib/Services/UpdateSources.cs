namespace ServiceLib.Services;

/// <summary>Defaults stay in Global; configuration stores only user overrides.</summary>
public static partial class UpdateSources
{
    public static readonly ECoreType[] Programs = [ECoreType.v2rayN, ECoreType.Xray, ECoreType.mihomo, ECoreType.sing_box];

    public static string Repository(Config config, ECoreType type)
    {
        var value = config.CheckUpdateItem.RepositoryOverrides?.GetValueOrDefault(type.ToString());
        return string.IsNullOrWhiteSpace(value) ? Global.CoreUrls[type] : NormalizeRepository(value);
    }

    public static string NormalizeRepository(string value)
    {
        value = value.Trim().TrimEnd('/');
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host != "github.com"
                || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException(UpdateSourceText.InvalidRepository);
            value = uri.AbsolutePath.Trim('/');
            if (value.EndsWith("/releases", StringComparison.OrdinalIgnoreCase)) value = value[..^9];
        }
        if (!RepositoryPattern().IsMatch(value) || value.Split('/').Any(p => p is "." or ".."))
            throw new ArgumentException(UpdateSourceText.InvalidRepository);
        return value;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();

    // SemanticVersion's constructor converts malformed input to 0.0.0, so its TryParse cannot validate release tags.
    public static bool TryVersion(string? value, out SemanticVersion? version)
    {
        version = null;
        if (value == null || !VersionPattern().IsMatch(value)) return false;
        var numeric = value.TrimStart('v', 'V').Split('-', '+')[0].Split('.');
        if (numeric.Any(p => !int.TryParse(p, out _))) return false;
        version = new SemanticVersion(value);
        return true;
    }

    [GeneratedRegex(@"^[vV]?\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex VersionPattern();

    public static void ValidateTemplate(string? value, int arguments)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var expanded = arguments switch
        {
            1 => string.Format(value, "geoip"),
            2 => string.Format(value, "geoip", "geoip-cn", "cn"),
            _ => value
        };
        if ((arguments == 1 && !value.Contains("{0}"))
            || (arguments == 2 && !value.Contains("{1}") && !value.Contains("{2}"))
            || expanded.Contains('{') || expanded.Contains('}')
            || !Uri.TryCreate(expanded, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0)
            throw new ArgumentException(UpdateSourceText.InvalidTemplate);
    }

    public static string AssetName(CoreInfo core, string tag, string platform, Architecture arch, bool desktop)
    {
        var template = (platform, arch) switch
        {
            ("windows", Architecture.X64) => core.DownloadUrlWin64,
            ("windows", Architecture.Arm64) => core.DownloadUrlWinArm64,
            ("linux", Architecture.X64) => core.DownloadUrlLinux64,
            ("linux", Architecture.Arm64) => core.DownloadUrlLinuxArm64,
            ("linux", Architecture.RiscV64) => core.DownloadUrlLinuxRiscV64,
            ("linux", Architecture.LoongArch64) => core.DownloadUrlLinuxLoong64,
            ("macos", Architecture.X64) => core.DownloadUrlOSX64,
            ("macos", Architecture.Arm64) => core.DownloadUrlOSXArm64,
            _ => null
        };
        if (template == null) throw new NotSupportedException(ResUI.MsgNotSupport);
        var name = Path.GetFileName(template);
        if (core.CoreType == ECoreType.v2rayN && platform == "windows" && desktop)
            name = name.Replace(".zip", "-desktop.zip");
        return string.Format(name, tag, tag.TrimStart('v', 'V'));
    }

    public static GitHubRelease SelectRelease(IEnumerable<GitHubRelease> releases, bool prerelease, SemanticVersion? maximum)
    {
        return releases.Where(r => !r.Draft && (prerelease || !r.Prerelease))
            .Where(r => TryVersion(r.TagName, out var v) && (maximum == null || v! <= maximum))
            .OrderByDescending(r => new SemanticVersion(r.TagName))
            .FirstOrDefault() ?? throw new InvalidOperationException(UpdateSourceText.NoRelease);
    }

    public static bool IsSourceSwitch(string? installed, string target) =>
        !string.Equals(installed, target, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Shared strings for both desktop frontends.</summary>
public static class UpdateSourceText
{
    private static string T(string zh, string en) =>
        System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
    public static string Title => T("更新与资源来源", "Update and resource sources");
    public static string Reset => T("恢复默认", "Restore default");
    public static string ResetAll => T("全部恢复默认", "Restore all defaults");
    public static string Default => T("默认：", "Default: ");
    public static string Builtin => T("内置路由模板", "Built-in routing templates");
    public static string Help => T("留空使用默认来源。恢复默认和修改来源在保存后生效，不会立即安装更新或修改现有路由。", "Leave blank to use defaults. Source changes and resets apply on save; they do not install updates or change existing routes.");
    public static string RepositoryHelp => T("GitHub：owner/repo 或仓库链接；需提供兼容当前平台的 Release 安装包。", "GitHub: owner/repo or repository URL; compatible release assets are required.");
    public static string InvalidRepository => T("请输入有效的 GitHub owner/repo 或 https://github.com/owner/repo 链接。", "Enter a valid GitHub owner/repo or https://github.com/owner/repo URL.");
    public static string InvalidTemplate => T("请输入有效的 HTTP(S) 地址，并保留示例中的占位符。", "Enter a valid HTTP(S) URL with the placeholders shown in the example.");
    public static string NoRelease => T("没有符合条件且版本有效的 Release。", "No eligible release with a valid version was found.");
    public static string Switch => T("切换来源", "Switch source");
    public static string Unknown => T("未知", "Unknown");
    public static string Source => T("更新来源", "Update source");
    public static string Installed => T("已安装来源", "Installed source");
    public static string Failed => T("更新失败", "Update failed");
}
