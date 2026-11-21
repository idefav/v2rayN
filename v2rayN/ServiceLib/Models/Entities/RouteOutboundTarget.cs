namespace ServiceLib.Models.Entities;

public sealed record RouteOutboundTarget
{
    public List<string>? ProfileIds { get; init; }
    public string? SubscriptionId { get; init; }
    // A display hint for exported rules and deleted subscriptions. Resolution always uses the ID.
    public string? SubscriptionName { get; init; }

    internal string Tag()
    {
        var identity = SubscriptionId != null
            ? "sub:" + SubscriptionId
            : "ids:" + JsonSerializer.Serialize((ProfileIds ?? []).Distinct().Order(StringComparer.Ordinal));
        return "route-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
