namespace ServiceLib.Handler;

public static class SubscriptionHandler
{
    public static async Task UpdateProcess(Config config, string subId, bool blProxy, Func<bool, string, Task> updateFunc)
    {
        var result = await UpdateWithResultAsync(config, subId, blProxy, updateFunc, AppManager.Instance.ExitToken);
        if (!AppManager.Instance.IsExiting)
            await updateFunc(result.Imported, ResUI.MsgUpdateSubscriptionEnd);
    }

    internal static async Task<SubscriptionUpdateResult> UpdateWithResultAsync(Config config, string subId, bool blProxy,
        Func<bool, string, Task> progress, CancellationToken token = default)
    {
        string? notificationError = null;
        async Task Notify(bool success, string message)
        {
            if (token.IsCancellationRequested) return;
            try { await progress(success, message); }
            catch (Exception ex)
            {
                notificationError = ex.Message;
                Logging.SaveLog("Subscription notification", ex);
            }
        }
        Func<bool, string, Task> updateFunc = Notify;
        var attempts = new List<SubscriptionAttempt>();
        token.ThrowIfCancellationRequested();
        await Notify(false, ResUI.MsgUpdateSubscriptionStart);
        var subItem = await AppManager.Instance.SubItems();

        if (subItem is not { Count: > 0 })
        {
            await updateFunc?.Invoke(false, ResUI.MsgNoValidSubscription);
            return new(attempts, notificationError);
        }

        foreach (var item in subItem)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!IsValidSubscription(item, subId))
                {
                    continue;
                }

                var hashCode = $"{item.Remarks}->";
                if (item.Enabled == false)
                {
                    attempts.Add(new(item.Id, SubscriptionAttemptStatus.Skipped));
                    await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgSkipSubscriptionUpdate}");
                    continue;
                }

                // Create download handler
                var downloadHandle = CreateDownloadHandler(item, hashCode, updateFunc);
                await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgStartGettingSubscriptions}");

                // Get all subscription content (main subscription + additional subscriptions)
                var result = await DownloadAllSubscriptions(config, item, blProxy, downloadHandle, token);
                token.ThrowIfCancellationRequested();

                // Process download result
                var imported = await ProcessDownloadResult(config, item.Id, result, hashCode, updateFunc, token);
                attempts.Add(new(item.Id, imported ? SubscriptionAttemptStatus.Succeeded : SubscriptionAttemptStatus.Failed));

                await updateFunc?.Invoke(false, "-------------------------------------------------------");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                attempts.Add(new(item.Id, SubscriptionAttemptStatus.Cancelled));
                break;
            }
            catch (Exception ex)
            {
                attempts.Add(new(item.Id, SubscriptionAttemptStatus.Failed));
                var hashCode = $"{item.Remarks}->";
                Logging.SaveLog("UpdateSubscription", ex);
                await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgFailedImportSubscription}: {ex.Message}");
                await updateFunc?.Invoke(false, "-------------------------------------------------------");
            }
        }

        return new(attempts, notificationError);
    }

    private static bool IsValidSubscription(SubItem item, string subId)
    {
        var id = item.Id.TrimEx();
        var url = item.Url.TrimEx();

        if (id.IsNullOrEmpty() || url.IsNullOrEmpty())
        {
            return false;
        }

        if (subId.IsNotEmpty() && item.Id != subId)
        {
            return false;
        }

        if (!url.StartsWith(Global.HttpsProtocol) && !url.StartsWith(Global.HttpProtocol))
        {
            return false;
        }

        return true;
    }

    private static DownloadService CreateDownloadHandler(SubItem item, string hashCode, Func<bool, string, Task> updateFunc)
    {
        if (!HttpRequestHeadersHelper.TryParse(item.RequestHeaders, out var requestHeaders))
        {
            throw new FormatException(ResUI.SubRequestHeadersInvalid);
        }

        var downloadHandle = new DownloadService
        {
            AcceptHeader = "*/*",
            RequestHeaders = requestHeaders
        };
        downloadHandle.Error += (sender2, args) =>
        {
            updateFunc?.Invoke(false, $"{hashCode}{args.GetException().Message}");
        };
        return downloadHandle;
    }

    private static async Task<string> DownloadSubscriptionContent(DownloadService downloadHandle, string url, bool blProxy, string userAgent, CancellationToken token)
    {
        var result = await downloadHandle.TryDownloadString(url, blProxy, userAgent, cancellationToken: token);
        token.ThrowIfCancellationRequested();

        // If download with proxy fails, try direct connection
        if (blProxy && result.IsNullOrEmpty())
        {
            result = await downloadHandle.TryDownloadString(url, false, userAgent, cancellationToken: token);
        }

        return result ?? string.Empty;
    }

    private static async Task<string> DownloadAllSubscriptions(Config config, SubItem item, bool blProxy, DownloadService downloadHandle, CancellationToken token)
    {
        // Download main subscription content
        var result = await DownloadMainSubscription(config, item, blProxy, downloadHandle, token);
        if (string.IsNullOrWhiteSpace(result)) return string.Empty;

        // Process additional subscription links (if any)
        if (item.ConvertTarget.IsNullOrEmpty() && item.MoreUrl.TrimEx().IsNotEmpty())
        {
            result = await DownloadAdditionalSubscriptions(item, result, blProxy, downloadHandle, token);
        }

        return result;
    }

    private static async Task<string> DownloadMainSubscription(Config config, SubItem item, bool blProxy, DownloadService downloadHandle, CancellationToken token)
    {
        // Prepare subscription URL and download directly
        var url = Utils.GetPunycode(item.Url.TrimEx());

        // If conversion is needed
        if (item.ConvertTarget.IsNotEmpty())
        {
            var subConvertUrl = config.ConstItem.SubConvertUrl.IsNullOrEmpty()
                ? Global.SubConvertUrls.FirstOrDefault()
                : config.ConstItem.SubConvertUrl;

            url = string.Format(subConvertUrl!, Utils.UrlEncode(url));

            if (!url.Contains("target="))
            {
                url += $"&target={item.ConvertTarget}";
            }

            if (!url.Contains("config="))
            {
                url += $"&config={Global.SubConvertConfig.FirstOrDefault()}";
            }
        }

        // Download and return result directly
        return await DownloadSubscriptionContent(downloadHandle, url, blProxy, item.UserAgent, token);
    }

    private static async Task<string> DownloadAdditionalSubscriptions(SubItem item, string mainResult, bool blProxy, DownloadService downloadHandle, CancellationToken token)
    {
        var result = mainResult;

        // If main subscription result is Base64 encoded, decode it first
        if (result.IsNotEmpty() && Utils.IsBase64String(result))
        {
            result = Utils.Base64Decode(result);
        }

        // Process additional URL list
        var lstUrl = item.MoreUrl.TrimEx().Split(",") ?? [];
        foreach (var it in lstUrl)
        {
            var url2 = Utils.GetPunycode(it);
            if (url2.IsNullOrEmpty())
            {
                continue;
            }

            token.ThrowIfCancellationRequested();
            var additionalResult = await DownloadSubscriptionContent(downloadHandle, url2, blProxy, item.UserAgent, token);

            if (string.IsNullOrWhiteSpace(additionalResult)) return string.Empty;
            result += Environment.NewLine + (Utils.IsBase64String(additionalResult) ? Utils.Base64Decode(additionalResult) : additionalResult);
        }

        return result;
    }

    private static async Task<bool> ProcessDownloadResult(Config config, string id, string result, string hashCode, Func<bool, string, Task> updateFunc, CancellationToken token)
    {
        if (result.IsNullOrEmpty())
        {
            await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgSubscriptionDecodingFailed}");
            return false;
        }

        await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgGetSubscriptionSuccessfully}");

        // If result is too short, display content directly
        if (result.Length < 99)
        {
            await updateFunc?.Invoke(false, $"{hashCode}{result}");
        }

        await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgStartParsingSubscription}");

        // Add servers to configuration
        var ret = await ConfigHandler.AddBatchServers(config, result, id, true, token);
        if (ret < 0)
        {
            Logging.SaveLog("FailedImportSubscription");
            Logging.SaveLog(result);
        }

        // Update completion message
        await updateFunc?.Invoke(false, ret >= 0
                ? $"{hashCode}{ResUI.MsgUpdateSubscriptionEnd}"
                : $"{hashCode}{ResUI.MsgFailedImportSubscription}");

        return ret >= 0;
    }
}
