namespace ServiceLib.Tests.Services;

public class TunAuthorizationTests
{
    [Test]
    public async Task ConcurrentRequestsSharePromptAndValidateBeforeSuccess()
    {
        var auth = new TunAuthorization();
        var prompt = new TaskCompletionSource<string?>();
        var prompts = 0; var validations = 0;
        Task<string?> Ask() { prompts++; return prompt.Task; }
        Task<bool> Validate(string p, CancellationToken _) { validations++; return Task.FromResult(p == "fixture"); }
        var first = auth.RequestAsync(Ask, Validate, default);
        var second = auth.RequestAsync(Ask, Validate, default);
        prompt.SetResult("fixture");
        if ((await first).State != TunAuthorizationState.Authorized || !ReferenceEquals(first, second)
            || prompts != 1 || validations != 1) throw new Exception("Authorization was duplicated or skipped validation");
    }

    [Test]
    public async Task CancellationAndBadPasswordPermitExplicitRetry()
    {
        var auth = new TunAuthorization();
        var cancelled = await auth.RequestAsync(() => Task.FromResult<string?>(null), (_, _) => throw new Exception("must not validate"), default);
        var failed = await auth.RequestAsync(() => Task.FromResult<string?>("bad"), (_, _) => Task.FromResult(false), default);
        var success = await auth.RequestAsync(() => Task.FromResult<string?>("good"), (_, _) => Task.FromResult(true), default);
        if (cancelled.State != TunAuthorizationState.Cancelled || failed.State != TunAuthorizationState.Failed
            || failed.Password != null || success.State != TunAuthorizationState.Authorized) throw new Exception("Retry state incorrect");
    }

    [Test]
    public async Task ChangedConfigurationDiscardsLatePassword()
    {
        var auth = new TunAuthorization();
        var prompt = new TaskCompletionSource<string?>();
        var pending = auth.RequestAsync(() => prompt.Task, (_, _) => Task.FromResult(true), default);
        auth.Invalidate();
        prompt.SetResult("unused");
        var result = await pending;
        if (result.State != TunAuthorizationState.Superseded || result.Password != null) throw new Exception("Stale authorization applied");
    }

    [Test]
    public async Task ExitDoesNotWaitForPasswordDialog()
    {
        var auth = new TunAuthorization();
        var prompt = new TaskCompletionSource<string?>();
        using var exit = new CancellationTokenSource();
        var pending = auth.RequestAsync(() => prompt.Task, (_, _) => Task.FromResult(true), exit.Token);
        exit.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        prompt.SetResult("late");
        if (result.Password != null || result.State != TunAuthorizationState.Superseded) throw new Exception("Exit authorization was accepted");
    }
}
