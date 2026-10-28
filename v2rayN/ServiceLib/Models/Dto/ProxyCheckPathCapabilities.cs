namespace ServiceLib.Models.Dto;

// Static transport limits are not probe results and never contribute to statistics.
public static class ProxyCheckPathCapabilities
{
    public static IReadOnlyList<string> UnsupportedHttpKinds { get; } = Array.AsReadOnly(new[] { "udp", "stun", "dns-udp", "h3" });
    public static bool IsFixedUnsupported(string path, string kind) => path == "http-connect" && UnsupportedHttpKinds.Contains(kind);
    public static string Title => ProxyCheckText.T("路径能力说明", "Path capabilities");
    public static string Summary => ProxyCheckText.T("4 条固定说明 · 未执行 · 不计入检测统计", "4 static notes · Not executed · Excluded from statistics");
    public static string Description => ProxyCheckText.T(
        "普通 HTTP CONNECT 只建立 TCP 隧道，不承载以下流量：\nHTTP CONNECT → UDP\nHTTP CONNECT → STUN/UDP\nHTTP CONNECT → DNS/UDP\nHTTP CONNECT → QUIC/HTTP3\n\n以上组合未执行，不产生结果行或采样，不计入进度、故障或未覆盖统计。SOCKS5 与普通套接字的实际检测仍保留；TUN 是否接管以及实际出口须以对应流量观测为准。此说明不涉及 CONNECT-UDP、MASQUE 或自定义扩展。",
        "Ordinary HTTP CONNECT creates a TCP tunnel and does not carry:\nHTTP CONNECT → UDP\nHTTP CONNECT → STUN/UDP\nHTTP CONNECT → DNS/UDP\nHTTP CONNECT → QUIC/HTTP3\n\nThese combinations are not executed and create no result rows or samples. They are excluded from progress, failure and coverage counts. Actual SOCKS5 and native socket probes remain; TUN capture and egress must be evaluated from the corresponding traffic. This does not describe CONNECT-UDP, MASQUE or custom extensions.");
    public static string NativePath(bool tunEnabled) => tunEnabled
        ? ProxyCheckText.T("系统网络栈（TUN 开启）", "System network stack (TUN on)")
        : ProxyCheckText.T("普通套接字（TUN 关闭）", "Native sockets (TUN off)");
}
