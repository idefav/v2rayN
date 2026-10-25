namespace ServiceLib.Models.Dto;

public sealed class ObserverProfile
{
    public int Version { get; set; }
    public string ServerId { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Ipv4 { get; set; } = "";
    public string HttpsUrl { get; set; } = "";
    public string HttpUrl { get; set; } = "";
    public string Http3Url { get; set; } = "";
    public string TcpTarget { get; set; } = "";
    public string TlsTarget { get; set; } = "";
    public string UdpTarget { get; set; } = "";
    public string StunTarget { get; set; } = "";
    public string DnsTarget { get; set; } = "";
}

public sealed class ObserverTicket
{
    public string SessionId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Query { get; set; } = "";
    public string QueryName { get; set; } = "";
    public DateTimeOffset Expires { get; set; }
}

public sealed class ObserverReceipt
{
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
    public string KeyId { get; set; } = "";
}

public sealed class EgressObservation
{
    public int Version { get; set; }
    public string ServerId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string ProbeId { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string PeerIp { get; set; } = "";
    public int PeerPort { get; set; }
    public string LocalEndpoint { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; }
    public string Query { get; set; } = "";
    public string QueryType { get; set; } = "";
    public string Alpn { get; set; } = "";
}

public sealed class ProxyProbeSample
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Target { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Connectivity { get; set; } = "pending";
    public string FailureStage { get; set; } = "";
    public string Error { get; set; } = "";
    public double Elapsed { get; set; }
    public List<EgressObservation> Observations { get; set; } = [];
    public List<ObserverReceipt> Receipts { get; set; } = [];
    public string Expected { get; set; } = "";
    public string Comparison { get; set; } = "unknown";
}
