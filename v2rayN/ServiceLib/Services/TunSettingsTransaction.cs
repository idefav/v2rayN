namespace ServiceLib.Services;

public sealed record TunApplyResult(bool Success, bool Restored, string Message);

public static class TunSettingsTransaction
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Only the TUN section is replaced, so other settings drafts cannot be overwritten.</summary>
    public static async Task<TunApplyResult> ApplyAsync(Config config, TunModeItem draft,
        Func<Task<bool>> persist, Func<string, Task<bool>>? reload)
    {
        await Gate.WaitAsync();
        try
        {
            var previous = JsonUtils.DeepCopy(config.TunModeItem)!;
            config.TunModeItem = JsonUtils.DeepCopy(draft)!;
            try
            {
                if (!await persist()) throw new IOException(TunSettingsText.T("无法保存 TUN 配置。", "Could not save TUN settings."));
                if (draft.EnableTun && reload != null && !await reload(TunSettingsText.T("TUN 设置已修改", "TUN settings changed")))
                    throw new IOException(TunSettingsText.T("TUN 配置启动失败。", "TUN configuration failed to start."));
                return new(true, false, draft.EnableTun && reload != null
                    ? TunSettingsText.T("设置已保存并应用；网络可用性请通过代理检查验证。", "Settings saved and applied; use Proxy check to verify connectivity.")
                    : TunSettingsText.T("设置已保存，开启 TUN 后生效。", "Saved; settings take effect when TUN is enabled."));
            }
            catch (Exception ex)
            {
                config.TunModeItem = previous;
                var restored = false;
                try
                {
                    restored = await persist();
                    if (previous.EnableTun && reload != null)
                        restored = await reload(TunSettingsText.T("TUN 应用失败，恢复上一份配置", "TUN apply failed; restoring previous configuration")) && restored;
                }
                catch (Exception rollback) { Logging.SaveLog("TunSettingsRollback", rollback); }
                return new(false, restored, ex.Message + "\n" + (restored
                    ? TunSettingsText.T("已恢复上一份设置和运行配置；当前修改仍保留在草稿中。", "Previous settings/configuration restored; your edits remain in the draft.")
                    : TunSettingsText.T("恢复未完成，请检查日志及连接状态。", "Recovery did not complete; inspect logs and connection status.")));
            }
        }
        finally { Gate.Release(); }
    }
}
