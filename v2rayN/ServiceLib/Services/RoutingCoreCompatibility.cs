using System.Globalization;

namespace ServiceLib.Services;

public enum RoutingCoreChoice { Cancel, UseSingbox, EditRule }

public sealed record RoutingCorePrompt(string Message, bool CanUseSingbox, CancellationToken CancellationToken);

/// <summary>Checks a copy of the requested topology without touching the running core or saved profile.</summary>
public static class RoutingCoreCompatibility
{
    public static string T(string zh, string en)
        => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;

    public static async Task<string?> CheckAlternativeAsync(Config config, ProfileItem profile, CancellationToken token)
        => await CheckAlternativeAsync(config, profile, token, (context, path, ct) => CheckFileAsync(context.RunCoreType, path, ct));

    internal static async Task<string?> CheckAlternativeAsync(Config config, ProfileItem profile, CancellationToken token,
        Func<CoreConfigContext, string, CancellationToken, Task<string?>> check)
    {
        // Custom/group/raw outbound configurations may embed core-specific semantics.
        if (profile.ConfigType is EConfigType.Custom or EConfigType.Outbound || profile.ConfigType.IsGroupType())
            return T("此类配置不能自动更换核心，请手动检查。", "This configuration requires a manual core compatibility review.");
        if (ConversionError(profile) is { } conversionError) return conversionError;
        var candidate = JsonUtils.DeepCopy(profile);
        candidate.CoreType = ECoreType.sing_box;
        var all = await CoreConfigContextBuilder.BuildAll(JsonUtils.DeepCopy(config), candidate);
        token.ThrowIfCancellationRequested();
        var validation = all.CombinedValidatorResult;
        // The builder can replace unsupported routing outbounds with the default proxy.
        // Do not offer an automatic conversion when any part needs user review.
        if (validation.Errors.Count + validation.Warnings.Count > 0)
            return string.Join(Environment.NewLine, validation.Errors.Concat(validation.Warnings));
        foreach (var context in new[] { all.MainResult.Context, all.PreSocksResult?.Context })
        {
            if (context == null) continue;
            foreach (var node in context.AllProxiesMap.Values.Append(context.Node))
                if (ConversionError(node) is { } linkedError) return linkedError;
            if (context.FullConfigTemplate?.Enabled == true)
                return T("已启用完整配置模板，请手动检查核心兼容性。", "A full configuration template is enabled; review compatibility manually.");
            var path = Utils.GetBinConfigPath($"compatibility-{Guid.NewGuid():N}.json");
            try
            {
                var result = await CoreConfigHandler.GenerateClientConfig(context, path);
                if (!result.Success) return result.Msg ?? T("生成候选配置失败。", "Failed to generate candidate configuration.");
                var error = await check(context, path, token);
                if (error != null) return error;
            }
            finally { if (!ProcessService.HasUnstoppedConfig(path)) File.Delete(path); }
        }
        return null;
    }

    internal static string? ConversionError(ProfileItem profile)
    {
        var fields = new List<string>();
        if (!string.IsNullOrEmpty(profile.CertSha)) fields.Add(nameof(profile.CertSha));
        if (!string.IsNullOrEmpty(profile.VerifyPeerCertByName)) fields.Add(nameof(profile.VerifyPeerCertByName));
        if (!string.IsNullOrEmpty(profile.Mldsa65Verify)) fields.Add(nameof(profile.Mldsa65Verify));
        if (!string.IsNullOrEmpty(profile.Finalmask)) fields.Add(nameof(profile.Finalmask));
        if (!string.IsNullOrEmpty(profile.SpiderX)) fields.Add(nameof(profile.SpiderX));
        var encryption = profile.GetProtocolExtra().VlessEncryption;
        if (!string.IsNullOrEmpty(encryption) && encryption != "none") fields.Add("VlessEncryption");
        return fields.Count == 0 ? null : T("以下节点选项无法保留到 sing-box，请手动检查：", "These profile options cannot be preserved in sing-box; review manually: ")
            + string.Join(", ", fields);
    }

    internal static async Task<string?> CheckFileAsync(ECoreType core, string path, CancellationToken token)
    {
        var info = CoreInfoManager.Instance.GetCoreInfo(core);
        if (info == null || core is not (ECoreType.sing_box or ECoreType.Xray))
            return T("此核心不支持自动配置检查。", "Automatic configuration checks are unavailable for this core.");
        var executable = CoreInfoManager.Instance.GetCoreExecFile(info, out var error);
        if (string.IsNullOrEmpty(executable)) return error;
        return await CheckExecutableAsync(executable, core, path, info.Environment, token);
    }

    internal static async Task<string?> CheckExecutableAsync(string executable, ECoreType core, string path,
        IDictionary<string, string?> environment, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = new ProcessService(executable, "", Utils.GetBinConfigPath(), true, false,
            environment.ToDictionary(p => p.Key, p => p.Value == null ? null! : string.Format(p.Value, path)), null) { ConfigPath = path };
        process.SetArguments(core == ECoreType.sing_box
            ? new[] { "check", "-c", path, "--disable-color" } : new[] { "run", "-test", "-config", path });
        try
        {
            try
            {
                await process.StartAsync(token: timeout.Token);
                await process.WaitForWrapperExitAsync(timeout.Token);
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var stopped = await process.StopAndWaitAsync(cleanup.Token);
                if (!stopped.Success) throw new IOException("配置检查进程退出未确认 / Configuration checker exit unconfirmed: " + stopped.Error);
            }
            return process.ExitCode == 0 ? null
                : T("核心配置检查失败：", "Core configuration check failed: ") + process.RecentError;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return T("核心配置检查超时。", "Core configuration check timed out."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return T("核心配置检查失败：", "Core configuration check failed: ") + ex.Message; }
    }
}
