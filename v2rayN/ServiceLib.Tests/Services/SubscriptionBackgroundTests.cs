using ServiceLib.Tests.CoreConfig;

namespace ServiceLib.Tests.Services;

[NotInParallel]
public class SubscriptionBackgroundTests
{
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(100_000);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CallbackOrPersistenceFailureDoesNotRedownloadEveryMinute(bool saveFails)
    {
        var clock = new Clock();
        var schedule = new SubscriptionUpdateSchedule(clock);
        var item = new SubItem { Id = "fixture", AutoUpdateInterval = 10 };
        var downloads = 0; var saves = 0; var callbacks = 0; var errors = new List<string>();
        Task Tick() => schedule.RunAsync([item], (_, _) =>
        {
            downloads++;
            return Task.FromResult(new SubscriptionUpdateResult([new(item.Id, SubscriptionAttemptStatus.Succeeded)]));
        }, (_, time) =>
        {
            saves++;
            if (saveFails) throw new IOException("timestamp fixture");
            item.UpdateTime = time;
            return Task.CompletedTask;
        }, (_, _) => { callbacks++; throw new InvalidOperationException("UI fixture"); }, (stage, _) => errors.Add(stage));
        await Tick();
        for (var i = 1; i < 10; i++) { clock.Now += TimeSpan.FromMinutes(1); await Tick(); }
        await downloads.Should().BeEqualTo(1);
        await callbacks.Should().BeEqualTo(1);
        clock.Now += TimeSpan.FromMinutes(1); await Tick();
        await downloads.Should().BeEqualTo(2);
        await saves.Should().BeEqualTo(2);
        await errors.Count.Should().BeEqualTo(saveFails ? 4 : 2);
    }

    [Test]
    public async Task OneSubscriptionFailureDoesNotBlockOthersAndCancelledSkippedAreNotRecorded()
    {
        var schedule = new SubscriptionUpdateSchedule(new Clock());
        var items = new[] { "throws", "ok", "cancelled", "skipped" }.Select(id => new SubItem { Id = id, AutoUpdateInterval = 5 }).ToArray();
        var saved = new List<string>();
        await schedule.RunAsync(items, (item, _) =>
        {
            if (item.Id == "throws") throw new IOException("download fixture");
            return Task.FromResult(new SubscriptionUpdateResult([new(item.Id, item.Id switch
            {
                "cancelled" => SubscriptionAttemptStatus.Cancelled, "skipped" => SubscriptionAttemptStatus.Skipped,
                _ => SubscriptionAttemptStatus.Succeeded
            })]));
        }, (id, _) => { saved.Add(id); return Task.CompletedTask; }, (_, _) => Task.CompletedTask, (_, _) => { });
        await string.Join(",", saved).Should().BeEqualTo("throws,ok");
    }

    [Test]
    public async Task ConcurrentTicksShareOneAttemptAndCancellationDoesNotMarkCompletion()
    {
        var schedule = new SubscriptionUpdateSchedule(new Clock());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new SubItem { Id = "one", AutoUpdateInterval = 5 };
        var calls = 0;
        Task Run(CancellationToken token = default) => schedule.RunAsync([item], async (_, ct) =>
        {
            calls++; entered.TrySetResult(); await release.Task.WaitAsync(ct);
            return new([new(item.Id, SubscriptionAttemptStatus.Succeeded)]);
        }, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, (_, _) => { }, token);
        using var cancel = new CancellationTokenSource();
        var cancelled = Run(cancel.Token); await entered.Task; cancel.Cancel();
        try { await cancelled; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        var first = Run(); var second = Run(); release.SetResult(); await Task.WhenAll(first, second);
        await calls.Should().BeEqualTo(2);
    }

    [Test]
    public async Task TimestampWritePreservesConcurrentSubscriptionEditsAndDoesNotRecreateDeletedRows()
    {
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        SQLiteHelper.Instance.CreateTable<SubItem>();
        var item = new SubItem { Id = Guid.NewGuid().ToString(), Url = "http://127.0.0.1/edited", AutoUpdateInterval = 90, UpdateTime = 10, Enabled = false };
        await SQLiteHelper.Instance.InsertAsync(item);
        try
        {
            await SQLiteHelper.Instance.SetSubscriptionUpdateTimeAsync(item.Id, 20);
            await SQLiteHelper.Instance.SetSubscriptionUpdateTimeAsync(item.Id, 15);
            var read = await AppManager.Instance.GetSubItem(item.Id);
            await read.UpdateTime.Should().BeEqualTo(20);
            await read.Url.Should().BeEqualTo(item.Url);
            await read.AutoUpdateInterval.Should().BeEqualTo(90);
            await read.Enabled.Should().BeFalse();
        }
        finally { await SQLiteHelper.Instance.DeleteAsync(item); }
        await (await SQLiteHelper.Instance.SetSubscriptionUpdateTimeAsync(item.Id, 30)).Should().BeEqualTo(0);
    }
}
