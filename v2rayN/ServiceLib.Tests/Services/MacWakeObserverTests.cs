namespace ServiceLib.Tests.Services;

[NotInParallel]
public class MacWakeObserverTests
{
    private const string ObjC = "/usr/lib/libobjc.dylib";

    [Test]
    public void NativeWakeNotificationUsesWorkspaceCenterAndUnregisters()
    {
        if (!OperatingSystem.IsMacOS()) { Skip.Test("Native macOS notification bridge"); return; }
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        var calls = 0;
        var observer = new v2rayN.Desktop.Common.MacWakeObserver(() => calls++);
        try
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            PostWake();
            if (calls != 1) throw new Exception("workspace wake notification did not reach pinned managed callback");
        }
        finally { observer.Dispose(); }
        observer.Dispose(); PostWake();
        if (calls != 1) throw new Exception("disposed wake observer still receives events");
    }

    [Test]
    public async Task CurrentMacNetworkAndProxyCanBeReadWithoutMutation()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("RECOVERY_LIVE_CHECK") != "1")
        { Skip.Test("Set RECOVERY_LIVE_CHECK=1 for read-only current Mac network acceptance"); return; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if (await MacRecoveryPlatform.NetworkAsync(timeout.Token) == null) throw new Exception("default network not ready");
        var actual = await MacRecoveryPlatform.ReadProxyAsync(10808, timeout.Token);
        if (!actual.Matches) throw new Exception("current system proxy mismatch: " + actual.Detail);
        if (!await RecoveryProbe.ProbeAsync(new("live read-only", 10808, "https://www.google.com/generate_204", 0), timeout.Token))
            throw new Exception("running proxy cannot reach Google");
    }

    private static void PostWake()
    {
        var workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
        var center = Send(workspace, sel_registerName("notificationCenter"));
        var utf8 = Marshal.StringToCoTaskMemUTF8("NSWorkspaceDidWakeNotification");
        try
        {
            var name = SendArgument(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8);
            Post(center, sel_registerName("postNotificationName:object:"), name, workspace);
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendArgument(nint receiver, nint selector, nint argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void Post(nint receiver, nint selector, nint name, nint obj);
}
