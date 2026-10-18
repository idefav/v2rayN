using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using ServiceLib.Services;

namespace v2rayN.Desktop.Common;

/// <summary>App-owned observers; deliberately independent of window activation/visibility.</summary>
internal sealed class MacConnectionEvents : IDisposable
{
    private readonly ConnectionRecoveryService _recovery;
    private readonly MacWakeObserver? _wake;
    private bool _disposed;

    public MacConnectionEvents(ConnectionRecoveryService recovery)
    {
        _recovery = recovery;
        NetworkChange.NetworkAddressChanged += AddressChanged;
        NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
        try { _wake = new MacWakeObserver(() => recovery.RequestCheck("system wake")); }
        catch (Exception ex) { Logging.SaveLog("Wake observer unavailable: " + ex.GetType().Name); }
    }

    private void AddressChanged(object? sender, EventArgs args) => _recovery.RequestCheck("network address changed");
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => _recovery.RequestCheck("network availability changed");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= AddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
        _wake?.Dispose();
    }
}

internal sealed class MacWakeObserver : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.dylib";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, Action> Actions = new();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NotificationCallback(nint self, nint selector, nint notification);
    private static readonly NotificationCallback Callback = Received;
    private static readonly Lazy<nint> ObserverClass = new(CreateClass);
    private nint _observer;
    private readonly nint _center;

    public MacWakeObserver(Action wake)
    {
        _observer = Send(ObserverClass.Value, Selector("new"));
        if (_observer == 0) throw new InvalidOperationException("Cannot create wake observer");
        Actions[_observer] = wake;
        var workspace = Send(objc_getClass("NSWorkspace"), Selector("sharedWorkspace"));
        _center = Send(workspace, Selector("notificationCenter"));
        var nameUtf8 = Marshal.StringToCoTaskMemUTF8("NSWorkspaceDidWakeNotification");
        try
        {
            var name = SendArgument(objc_getClass("NSString"), Selector("stringWithUTF8String:"), nameUtf8);
            AddObserver(_center, Selector("addObserver:selector:name:object:"), _observer, Selector("connectionDidWake:"), name, 0);
        }
        finally { Marshal.FreeCoTaskMem(nameUtf8); }
    }

    private static nint CreateClass()
    {
        var cls = objc_allocateClassPair(objc_getClass("NSObject"), "V2rayNConnectionWakeObserver", 0);
        if (cls == 0) throw new InvalidOperationException("Cannot register wake observer");
        if (!class_addMethod(cls, Selector("connectionDidWake:"), Marshal.GetFunctionPointerForDelegate(Callback), "v@:@"))
            throw new InvalidOperationException("Cannot register wake callback");
        objc_registerClassPair(cls);
        return cls;
    }

    private static void Received(nint self, nint selector, nint notification)
    {
        // Never unwind a managed exception through AppKit.
        try { if (Actions.TryGetValue(self, out var wake)) wake(); }
        catch (Exception ex) { Logging.SaveLog("Wake callback failed: " + ex.GetType().Name); }
    }

    public void Dispose()
    {
        var observer = Interlocked.Exchange(ref _observer, 0);
        if (observer == 0) return;
        Actions.TryRemove(observer, out _);
        SendArgument(_center, Selector("removeObserver:"), observer);
        Send(observer, Selector("release"));
    }

    private static nint Selector(string name) => sel_registerName(name);
    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(nint cls, nint selector, nint implementation, string types);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendArgument(nint receiver, nint selector, nint argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void AddObserver(nint receiver, nint selector, nint observer, nint callback, nint name, nint obj);
}
