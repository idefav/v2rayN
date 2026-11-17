namespace ServiceLib.Services;

internal sealed record CoreLaunchSnapshot(string OperationId, CoreConfigContext Context, CoreInfo Info,
    string Executable, CoreListenerPlan Listeners, bool Elevated, int ProxyPort);

internal sealed record ConnectionRuntimeState(bool MainReady, bool PreRequired, bool PreReady, bool Unconfirmed,
    bool Tun, CoreRuntimeSummary? Summary, bool? ProxyApplied, string? ProxyDetail)
{
    internal bool Ready => MainReady && (!PreRequired || PreReady) && !Unconfirmed;
    internal bool Incomplete => !Unconfirmed && PreRequired && (MainReady || PreReady) && !(MainReady && PreReady);
}
