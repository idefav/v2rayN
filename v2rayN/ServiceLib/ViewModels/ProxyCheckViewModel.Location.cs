namespace ServiceLib.ViewModels;

public partial class ProxyCheckViewModel
{
    private CancellationTokenSource? _locationCancel;
    private readonly HashSet<(ProxyDiagnosticResult Row, string Address)> _locationRequests = [];

    private void BeginLocationLookups()
    {
        CancelLocationLookups();
        _locationCancel?.Dispose();
        _locationCancel = new();
        _locationRequests.Clear();
    }

    private void CancelLocationLookups()
    {
        _locationCancel?.Cancel();
        foreach (var row in Results.Where(r => r.LocationState == "loading"))
        {
            row.LocationState = "unavailable";
            _rowCache.Remove(row.Id);
        }
        RefreshPresentation();
    }

    private void QueueLocationLookup(ProxyDiagnosticResult row)
    {
        if (_disposed || _locationCancel is not { IsCancellationRequested: false } cancel
            || string.IsNullOrWhiteSpace(row.Egress) || row.ObservationType == "not-applicable"
            || !_locationRequests.Add((row, row.Egress))) return;
        row.LocationState = "loading";
        _rowCache.Remove(row.Id);
        RefreshPresentation();
        _ = ResolveLocationAsync(row, row.Egress, cancel.Token);
    }

    private async Task ResolveLocationAsync(ProxyDiagnosticResult row, string address, CancellationToken token)
    {
        try
        {
            var provider = AppManager.Instance.Config.SpeedTestItem.IPAPIUrl;
            var queries = address.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()
                .Select(ip => EgressLocationService.Instance.LookupAsync(ip, provider, token));
            var locations = await Task.WhenAll(queries);
            RxSchedulers.MainThreadScheduler.Schedule(() =>
            {
                if (_disposed || token.IsCancellationRequested || !Results.Contains(row) || row.Egress != address) return;
                row.EgressLocations = locations.OfType<EgressLocation>().ToList();
                row.LocationState = row.EgressLocations.Count > 0 ? "success" : "unavailable";
                _rowCache.Remove(row.Id);
                RefreshPresentation();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
