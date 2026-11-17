namespace ServiceLib.Models.Dto;

/// <summary>Presentation facts captured only after the entire core topology is verified.</summary>
public sealed record CoreRuntimeSummary(string? ProfileId, string Server, string? Routing, bool Tun)
{
    public ECoreType? Core { get; init; }
    internal string Generation { get; init; } = Guid.NewGuid().ToString("N");
    internal int ProxyPort { get; init; }
}
