using System.Runtime.CompilerServices;
using ReactiveUI.Builder;

namespace ServiceLib.Tests;

internal static class TestBootstrap
{
    // Initialize before discovery or execution, including filtered test runs.
    // No test class should depend on another class running first.
    [ModuleInitializer]
    internal static void InitializeReactiveUI()
    {
        SysProxyHandler.ChangesEnabled = false;
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
    }
}
