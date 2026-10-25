namespace ServiceLib.ViewModels;

public partial class ProxyCheckViewModel
{
    [Reactive] public partial string ObserverDomain { get; set; } = "";
    [Reactive] public partial string ObserverPairingCode { get; set; } = "";
    [Reactive] public partial string ObserverDeviceName { get; set; } = "v2rayN " + (OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : "Linux");
    [Reactive] public partial string ObserverDeviceDetails { get; set; } = "";
    [Reactive] public partial bool CanUseTemporaryObserver { get; set; }
    [Reactive] public partial bool CanForgetObserver { get; set; }

    private void ApplyPairedConnection(ObserverConnection connection)
    {
        ObserverDomain = connection.Domain;
        ObserverProfileJson = JsonSerializer.Serialize(connection.Profile, ProxyCheckService.Json);
        UseObserver = true;
        using var ca = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(connection.CaPem);
        ObserverDeviceDetails = $"https://control.{connection.Domain}\nDevice: {connection.DeviceId}\nCA SHA-256: {Convert.ToHexString(SHA256.HashData(ca.PublicKey.ExportSubjectPublicKeyInfo()))}\nSigning SHA-256: {Convert.ToHexString(SHA256.HashData(ObserverSession.Decode(connection.Profile.PublicKey)))}";
    }

    public async Task PairObserverAsync(bool temporary = false)
    {
        if (_disposed || _starting || IsRunning || IsTestingObserver) return;
        if (!temporary) CanUseTemporaryObserver = false;
        IsTestingObserver = true; CanStart = CanRetry = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _observerCancel = timeout;
        try
        {
            ObserverConnectionState = "pairing";
            var service = ObserverPairingService.Instance;
            var connection = temporary ? await service.UseTemporaryAsync() : await service.PairAsync(ObserverDomain, ObserverPairingCode, ObserverDeviceName, timeout.Token);
            if (_disposed) return;
            using var identity = await service.OpenAsync(timeout.Token);
            _observerCancel = null;
            ApplyPairedConnection(connection);
            ObserverPairingCode = ""; CanUseTemporaryObserver = false;
            ObserverDeviceDetails += "\n" + T("证书到期：", "Certificate expires: ") + identity.Expires.ToString("u");
            ObserverConnectionState = service.IsTemporary ? "temporary" : "passed";
            ObserverStatus = service.IsTemporary ? T("仅本次使用；退出后需要重新配对。", "Temporary identity; pair again after exit.") : T("已配对，设备凭证已保存到系统安全存储。", "Paired; device credentials saved in system secure storage.");
        }
        catch (ObserverStorageException)
        {
            CanUseTemporaryObserver = ObserverPairingService.Instance.HasCandidate;
            ObserverConnectionState = "locked";
            ObserverStatus = T("安全存储不可用或授权被拒绝。可重试，或明确选择“仅本次使用”；不会写入明文凭证。", "Secure storage unavailable or access denied. Retry or explicitly select temporary use; credentials are never saved as plaintext.");
        }
        catch (Exception ex) { ObserverConnectionState = ex is ObserverAuthenticationException auth ? auth.State : "failed"; ObserverStatus = ex is OperationCanceledException ? T("配对已取消或超时；可用同一配对串重试。", "Pairing cancelled or timed out; retry the same pairing string.") : ex.Message; }
        finally { _observerCancel = null; IsTestingObserver = false; CanStart = !_disposed; CanRetry = !_disposed && Results.Any(r => r.State is "failed" or "cancelled"); }
    }

    public async Task UnpairObserverAsync(bool localOnly = false)
    {
        if (_disposed || _starting || IsRunning || IsTestingObserver) return;
        IsTestingObserver = true; CanStart = CanRetry = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); _observerCancel = timeout;
        try
        {
            await ObserverPairingService.Instance.UnpairAsync(localOnly, timeout.Token);
            UseObserver = false; ObserverProfileJson = ""; ObserverPairingCode = ""; ObserverDeviceDetails = "";
            CanForgetObserver = CanUseTemporaryObserver = false;
            ObserverConnectionState = "unverified";
            ObserverStatus = localOnly ? T("已删除本地凭证；服务端撤销未完成，请管理员撤销设备。", "Local credentials deleted; server revocation is incomplete. Ask the administrator to revoke the device.") : T("设备已撤销并删除本地凭证。", "Device revoked and local credentials deleted.");
        }
        catch (Exception) { CanForgetObserver = true; ObserverStatus = T("无法完成撤销。可以重试，或仅删除本地凭证（服务端撤销未完成）。", "Revocation could not complete. Retry or delete locally only (server revocation remains incomplete)."); }
        finally { _observerCancel = null; IsTestingObserver = false; CanStart = !_disposed; CanRetry = !_disposed && Results.Any(r => r.State is "failed" or "cancelled"); }
    }
}
