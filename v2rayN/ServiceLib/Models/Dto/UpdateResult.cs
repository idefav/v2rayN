namespace ServiceLib.Models.Dto;

public class UpdateResult
{
    public bool Success { get; set; }
    public string? Msg { get; set; }
    public SemanticVersion? Version { get; set; }
    public string? Url { get; set; }
    public string? Repository { get; set; }
    public string? Tag { get; set; }
    public bool IsSourceSwitch { get; set; }
    public string? FileName { get; set; }

    public UpdateResult(bool success, string? msg)
    {
        Success = success;
        Msg = msg;
    }

    public UpdateResult(bool success, SemanticVersion? version)
    {
        Success = success;
        Version = version;
    }
}
