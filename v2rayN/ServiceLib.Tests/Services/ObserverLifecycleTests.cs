using System.Security.Cryptography;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.Services;

public class ObserverLifecycleTests
{
    private static void Configure(ProxyCheckViewModel model)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        model.ImportObserverProfile(JsonSerializer.Serialize(ObserverTests.Profile(key), ProxyCheckService.Json));
        model.ObserverCredential = new string('k', 64);
    }

    [Test]
    public async Task ObserverTestIsExclusiveAndCanBeCancelled()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var model = new ProxyCheckViewModel(async (_, ct) =>
        {
            Interlocked.Increment(ref calls); entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return new FakeSession();
        });
        Configure(model);
        var pending = model.TestObserverAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await model.IsBusy.Should().BeTrue();
        await model.CanStart.Should().BeFalse();
        await model.TestObserverAsync();
        await model.StartAsync();
        await calls.Should().BeEqualTo(1);
        model.Cancel(); await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await model.IsBusy.Should().BeFalse();
        await model.CanStart.Should().BeTrue();
        await model.ObserverConnectionState.Should().BeEqualTo("unverified");
    }

    [Test]
    public async Task DisposeCancelsObserverTestAndPreventsRestart()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var model = new ProxyCheckViewModel(async (_, ct) =>
        {
            entered.TrySetResult(); await Task.Delay(System.Threading.Timeout.Infinite, ct); return new FakeSession();
        });
        Configure(model);
        var pending = model.TestObserverAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        model.Dispose(); await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await model.CanStart.Should().BeFalse();
        await model.ObserverCredential.Should().BeEqualTo("");
        await model.TestObserverAsync(); await model.IsBusy.Should().BeFalse();
    }

    [Test]
    public async Task ChangedSettingsCannotReceiveStaleSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession();
        using var model = new ProxyCheckViewModel(async (_, _) =>
        {
            entered.TrySetResult(); await release.Task; return session;
        });
        Configure(model);
        var pending = model.TestObserverAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Configure(model); release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await model.ObserverConnectionState.Should().BeEqualTo("unverified");
        await session.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task SuccessfulTestDisposesSessionAndInvalidatesAfterCredentialChange()
    {
        var session = new FakeSession();
        using var model = new ProxyCheckViewModel((_, _) => Task.FromResult<IAsyncDisposable>(session));
        Configure(model);
        // Pasted profiles can be normalized without cancelling their own test.
        model.ObserverProfileJson = JsonSerializer.Serialize(JsonSerializer.Deserialize<ObserverProfile>(model.ObserverProfileJson, ProxyCheckService.Json));
        await model.TestObserverAsync();
        await model.ObserverConnectionState.Should().BeEqualTo("passed");
        await session.Disposed.Should().BeTrue();
        model.ObserverCredential = "different-credential";
        await model.ObserverConnectionState.Should().BeEqualTo("unverified");
    }

    [Test]
    public async Task CredentialChangesRemovePreviousSuccessText()
    {
        using var model = new ProxyCheckViewModel((_, _) => Task.FromResult<IAsyncDisposable>(new FakeSession()));
        Configure(model);
        await model.TestObserverAsync();
        var success = ProxyCheckText.T("控制接口连接成功", "Control connection works");
        await model.ObserverStatus.Contains(success).Should().BeTrue();
        model.ObserverCredential = new string('n', 64);
        await model.ObserverConnectionState.Should().BeEqualTo("unverified");
        await model.ObserverStatus.Contains(success).Should().BeFalse();
    }

    [Test]
    public async Task ProfileChangesRemoveStaleIdentityAndDisablingClearsStatus()
    {
        using var model = new ProxyCheckViewModel((_, _) => Task.FromResult<IAsyncDisposable>(new FakeSession()));
        Configure(model);
        model.ObserverStatus = "fixture-prior-key-fingerprint";
        model.ObserverProfileJson = "{\"domain\":\"new.example.net\"}";
        await model.ObserverStatus.Contains("fixture-prior-key-fingerprint").Should().BeFalse();
        await model.ObserverConnectionState.Should().BeEqualTo("unverified");
        model.UseObserver = false;
        await model.ObserverStatus.Should().BeEqualTo("");
    }

    [Test]
    public async Task InvalidConfigurationNeverStartsAConnection()
    {
        var calls = 0;
        using var model = new ProxyCheckViewModel((_, _) => { calls++; return Task.FromResult<IAsyncDisposable>(new FakeSession()); });
        model.UseObserver = true; model.ObserverProfileJson = "{";
        await model.TestObserverAsync();
        await model.ObserverConnectionState.Should().BeEqualTo("invalid");
        Configure(model); model.ObserverCredential = "";
        await model.TestObserverAsync();
        await model.ObserverConnectionState.Should().BeEqualTo("invalid");
        await calls.Should().BeEqualTo(0);
        await model.CanStart.Should().BeTrue();
    }

    [Test]
    public async Task ConnectionFailureRestoresControlsWithoutClaimingSuccess()
    {
        using var model = new ProxyCheckViewModel((_, _) => Task.FromException<IAsyncDisposable>(new IOException("fixture unavailable")));
        Configure(model);
        await model.TestObserverAsync();
        await model.ObserverConnectionState.Should().BeEqualTo("failed");
        await model.IsBusy.Should().BeFalse();
        await model.CanStart.Should().BeTrue();
    }

    private sealed class FakeSession : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
