namespace ServiceLib.Models.Dto;

public partial class CheckUpdateModel : ReactiveObject
{
    public bool? IsSelected { get; set; }
    public bool IsCheckPreRelease { get; set; }
    public bool ShowCheckPreRelease { get; set; }
    public ECoreType? CoreType { get; set; }
    [Reactive] public partial string? Remarks { get; set; }
    [Reactive] public partial string? Source { get; set; }
    [Reactive] public partial string? InstalledSource { get; set; }
    [Reactive] public partial string? CurrentVersion { get; set; }
    public bool IsGeoFile { get; set; }
    public string CoreTypeForStorage => IsGeoFile ? "GeoFiles" : (CoreType?.ToString() ?? "");
}
