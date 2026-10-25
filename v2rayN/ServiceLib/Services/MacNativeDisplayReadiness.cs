namespace ServiceLib.Services;

public sealed record MacNativeDisplayState(bool SessionAvailable, bool ScreenLocked, uint MainDisplay, uint ActiveDisplays, int DisplayLinkStatus)
{
    public bool IsReady => SessionAvailable && !ScreenLocked && MainDisplay != 0 && ActiveDisplays > 0 && DisplayLinkStatus == 0;
    public override string ToString() => $"session={SessionAvailable}, locked={ScreenLocked}, main={MainDisplay}, active={ActiveDisplays}, CoreVideo={DisplayLinkStatus}";
}

/// <summary>
/// Do not initialize AppKit while the interactive display session is unavailable.
/// Avalonia 12.1.2 otherwise fails when it registers its native CVDisplayLink.
/// This waits for the user's session; it never unlocks or reconfigures displays.
/// </summary>
public static class MacNativeDisplayReadiness
{
    public static Task WaitUntilReadyAsync(TimeSpan timeout, Action<MacNativeDisplayState>? waiting = null, CancellationToken cancellationToken = default)
        => OperatingSystem.IsMacOS()
            ? WaitCoreAsync(ReadCurrent, timeout, waiting, cancellationToken)
            : Task.CompletedTask;

    internal static async Task WaitCoreAsync(Func<MacNativeDisplayState> probe, TimeSpan timeout,
        Action<MacNativeDisplayState>? waiting, CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (timeout < TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var elapsed = Stopwatch.StartNew();
        MacNativeDisplayState? previous = null;
        delay ??= static (duration, token) => Task.Delay(duration, token);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = probe();
            if (state.IsReady) return;
            if (state != previous) { waiting?.Invoke(state); previous = state; }
            if (timeout != System.Threading.Timeout.InfiniteTimeSpan && elapsed.Elapsed >= timeout)
                throw new TimeoutException($"macOS native display is unavailable ({state}). Unlock the Mac and keep a display awake, then retry native review. Headless results are separate.");
            var interval = TimeSpan.FromMilliseconds(500);
            if (timeout != System.Threading.Timeout.InfiniteTimeSpan)
                interval = TimeSpan.FromTicks(Math.Min(interval.Ticks, Math.Max(0, (timeout - elapsed.Elapsed).Ticks)));
            await delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("macos")]
    public static MacNativeDisplayState ReadCurrent()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var session = CGSessionCopyCurrentDictionary();
        var locked = false;
        try
        {
            if (session != IntPtr.Zero)
            {
                var key = CFStringCreateWithCString(IntPtr.Zero, "CGSSessionScreenIsLocked", 0x08000100);
                if (key == IntPtr.Zero) throw new InvalidOperationException("Cannot allocate display-session key.");
                try
                {
                    var value = CFDictionaryGetValue(session, key);
                    locked = value != IntPtr.Zero && CFGetTypeID(value) == CFBooleanGetTypeID() && CFBooleanGetValue(value);
                }
                finally { CFRelease(key); }
            }
            var status = CGGetActiveDisplayList(0, IntPtr.Zero, out var active);
            var result = CVDisplayLinkCreateWithActiveCGDisplays(out var link);
            if (result == 0 && link != IntPtr.Zero) CVDisplayLinkRelease(link);
            return new(session != IntPtr.Zero, locked, CGMainDisplayID(), status == 0 ? active : 0, result);
        }
        finally { if (session != IntPtr.Zero) CFRelease(session); }
    }

    private const string Graphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string Video = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport(Graphics)] private static extern IntPtr CGSessionCopyCurrentDictionary();
    [DllImport(Graphics)] private static extern uint CGMainDisplayID();
    [DllImport(Graphics)] private static extern int CGGetActiveDisplayList(uint maximum, IntPtr displays, out uint count);
    [DllImport(Video)] private static extern int CVDisplayLinkCreateWithActiveCGDisplays(out IntPtr link);
    [DllImport(Video)] private static extern void CVDisplayLinkRelease(IntPtr link);
    [DllImport(Foundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(Foundation)] private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
    [DllImport(Foundation)] private static extern nuint CFGetTypeID(IntPtr value);
    [DllImport(Foundation)] private static extern nuint CFBooleanGetTypeID();
    [DllImport(Foundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFBooleanGetValue(IntPtr value);
    [DllImport(Foundation)] private static extern void CFRelease(IntPtr value);
}
