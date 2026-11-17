namespace ServiceLib.Manager;

public class TaskManager
{
    private static readonly Lazy<TaskManager> _instance = new(() => new());
    public static TaskManager Instance => _instance.Value;
    private Config _config;
    private Func<bool, string, Task>? _updateFunc;
    private readonly SubscriptionUpdateSchedule _subscriptions = new();

    public void RegUpdateTask(Config config, Func<bool, string, Task> updateFunc)
    {
        _config = config;
        _updateFunc = updateFunc;

        Task.Run(ScheduledTasks);
    }

    private async Task ScheduledTasks()
    {
        Logging.SaveLog("Setup Scheduled Tasks");

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        var numOfExecuted = 1;
        while (!AppManager.Instance.IsExiting && await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            if (AppManager.Instance.IsExiting) break;
            //Execute once 1 minute
            try
            {
                await UpdateTaskRunSubscription();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ScheduledTasks - UpdateTaskRunSubscription", ex);
            }
            if (AppManager.Instance.IsExiting) break;

            //Execute once 20 minute
            if (numOfExecuted % 20 == 0)
            {
                //Logging.SaveLog("Execute save config");

                try
                {
                    await ConfigHandler.SaveConfig(_config);
                    await ProfileExManager.Instance.SaveTo();
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("ScheduledTasks - SaveConfig", ex);
                }
            }

            //Execute once 1 hour
            if (numOfExecuted % 60 == 0)
            {
                //Logging.SaveLog("Execute delete expired files");

                FileUtils.DeleteExpiredFiles(Utils.GetBinConfigPath(), DateTime.Now.AddHours(-1), "Test");
                FileUtils.DeleteExpiredFiles(Utils.GetLogPath(), DateTime.Now.AddDays(-7));
                FileUtils.DeleteExpiredFiles(Utils.GetTempPath(), DateTime.Now.AddDays(-7));

                try
                {
                    await UpdateTaskRunGeo(numOfExecuted / 60);
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("ScheduledTasks - UpdateTaskRunGeo", ex);
                }
            }

            //Execute once 24 hour
            if (numOfExecuted % 1440 == 1)
            {
                try
                {
                    await UpdateTaskRunCheckUpdate();
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("ScheduledTasks - UpdateTaskRunCheckUpdate", ex);
                }
            }
            numOfExecuted++;
        }
    }

    private async Task UpdateTaskRunSubscription()
    {
        await _subscriptions.RunAsync(await AppManager.Instance.SubItems(),
            async (item, token) =>
            {
                Logging.SaveLog("Execute update subscription");
                return await SubscriptionHandler.UpdateWithResultAsync(_config, item.Id, true,
                    (success, msg) => _updateFunc?.Invoke(success, msg) ?? Task.CompletedTask, token);
            },
            async (id, time) => { await SQLiteHelper.Instance.SetSubscriptionUpdateTimeAsync(id, time); },
            async (result, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (_updateFunc != null) await _updateFunc(result.Imported, ResUI.MsgUpdateSubscriptionEnd);
            }, Logging.SaveLog, AppManager.Instance.ExitToken);
    }

    private async Task UpdateTaskRunGeo(int hours)
    {
        if (_config.GuiItem.AutoUpdateInterval > 0 && hours > 0 && hours % _config.GuiItem.AutoUpdateInterval == 0)
        {
            Logging.SaveLog("Execute update geo files");

            await new UpdateService(_config, async (success, msg) =>
            {
                await _updateFunc?.Invoke(false, msg);
            }).UpdateGeoFileAll();
        }
    }

    private async Task UpdateTaskRunCheckUpdate()
    {
        Logging.SaveLog("Execute check update");

        var updateService = new UpdateService(_config, async (success, msg) => await Task.CompletedTask);

        var msgs = await updateService.CheckHasUpdateOnlyAll(_config.CheckUpdateItem.UpdateViaProxy);
        foreach (var msg in msgs)
        {
            await _updateFunc?.Invoke(false, msg);
        }
        NoticeManager.Instance.Enqueue(string.Join("\n", msgs));

        if (msgs.Count > 0)
        {
            AppEvents.HasUpdateNotified.Publish(true);
        }
    }
}
